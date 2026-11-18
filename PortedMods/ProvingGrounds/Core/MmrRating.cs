// Port of upstream/ProvingGrounds/plugin/src/MmrRating.h and MmrRating.cpp (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>Pure performance-weighted Elo (no Character / IO). C++: MmrRating.h:1-47, MmrRating.cpp:1-265 (PA §2.14).
/// <para>The arrays stand for the C++ pointers: indexed 0..fighterCount-1 like the C++, and at most 16 fighters (the C++'s
/// <c>perfMult[16]</c>/<c>pending[16]</c> locals; more would overflow there and throw here).</para></summary>
public static class MmrRating
{
    // C++: MmrRating.h:8-15
    public const float DefaultMmr = 100.0f;
    public const float FloorMmr = 0.0f;
    public const float MaxAbsDelta = 50.0f;
    public const float PerfMultMin = 0.75f;
    public const float PerfMultMax = 1.25f;
    public const int KEarly = 32;
    public const int KLate = 16;
    public const int KDecayMatches = 20;

    // C++: MmrRating.cpp:10-17
    static float Clamp(float v, float lo, float hi)
    {
        if (v < lo)
            return lo;
        if (v > hi)
            return hi;
        return v;
    }

    // C++: MmrRating.cpp:19-32
    static bool FighterWon(SparPodium.Snapshot snap, in SparPodium.FighterRow row)
    {
        switch (snap.Outcome)
        {
            case SparPodium.OutcomeKind.TeamAWins:
                return row.Team == MatchRules.MatchTeam.TeamA;
            case SparPodium.OutcomeKind.TeamBWins:
                return row.Team == MatchRules.MatchTeam.TeamB;
            case SparPodium.OutcomeKind.LastStanding:
                return row.Id == snap.LastStandingId;
            default:
                return false;
        }
    }

    // C++: MmrRating.cpp:34-47. One definition of "opponent" for both the Elo expectation and the team-size balance, so the two can
    // never disagree.
    static bool IsOpponentRow(SparPodium.Snapshot snap, in SparPodium.FighterRow self, in SparPodium.FighterRow other)
    {
        if (snap.Outcome == SparPodium.OutcomeKind.LastStanding ||
            snap.Mode == MatchRules.MatchMode.LastStanding)
            return true;
        return self.Team != MatchRules.MatchTeam.None &&
            other.Team != MatchRules.MatchTeam.None &&
            other.Team != self.Team;
    }

    // C++: MmrRating.cpp:49-73
    static float OpponentAverage(SparPodium.Snapshot snap, int selfIndex, float[] mmrBefore)
    {
        float sum = 0.0f;
        int n = 0;
        ref readonly SparPodium.FighterRow self = ref snap.Fighters[selfIndex];

        for (int i = 0; i < snap.FighterCount; ++i)
        {
            if (i == selfIndex)
                continue;

            if (!IsOpponentRow(snap, self, snap.Fighters[i]))
                continue;

            sum += mmrBefore[i];
            ++n;
        }

        if (n <= 0)
            return DefaultMmr;
        return sum / (float)n;
    }

    // C++: MmrRating.cpp:75-96. Rated team sizes around one fighter. Unrated fighters take no part in the rating economy, so they must
    // not dilute the team balance either.
    static void TeamSizes(SparPodium.Snapshot snap, int selfIndex, bool[]? rateMask, out int own, out int opp)
    {
        own = 1;
        opp = 0;
        ref readonly SparPodium.FighterRow self = ref snap.Fighters[selfIndex];
        for (int i = 0; i < snap.FighterCount; ++i)
        {
            if (i == selfIndex || (rateMask != null && !rateMask[i]))
                continue;
            if (IsOpponentRow(snap, self, snap.Fighters[i]))
                ++opp;
            else
                ++own;
        }
    }

    // C++: MmrRating.cpp:99-104
    public static bool ShouldRate(SparPodium.OutcomeKind outcome)
    {
        return outcome == SparPodium.OutcomeKind.TeamAWins
            || outcome == SparPodium.OutcomeKind.TeamBWins
            || outcome == SparPodium.OutcomeKind.LastStanding;
    }

    // C++: MmrRating.cpp:106-109
    public static int KFactor(int matchesBefore)
    {
        return matchesBefore >= KDecayMatches ? KLate : KEarly;
    }

    /// <summary>Maps MvpScore within the match to [PerfMultMin, PerfMultMax]. Equal scores give 1.0 for everyone.</summary>
    // C++: MmrRating.cpp:111-142
    public static void ComputePerfMultipliers(SparPodium.FighterRow[]? fighters, int fighterCount, float[]? outMult)
    {
        if (fighters == null || outMult == null || fighterCount <= 0)
            return;

        float minScore = 0.0f;
        float maxScore = 0.0f;
        for (int i = 0; i < fighterCount; ++i)
        {
            float s = SparPodium.ComputeMvpScore(fighters[i]);
            if (i == 0 || s < minScore)
                minScore = s;
            if (i == 0 || s > maxScore)
                maxScore = s;
        }

        float span = maxScore - minScore;
        for (int i = 0; i < fighterCount; ++i)
        {
            if (span <= 0.0001f)
            {
                outMult[i] = 1.0f;
                continue;
            }
            float s = SparPodium.ComputeMvpScore(fighters[i]);
            float t = (s - minScore) / span;
            outMult[i] = PerfMultMin + t * (PerfMultMax - PerfMultMin);
        }
    }

    /// <summary>Expected score vs opponent average (classic Elo).</summary>
    // C++: MmrRating.cpp:144-148
    public static float ExpectedScore(float mmr, float opponentAvgMmr)
    {
        float exponent = (opponentAvgMmr - mmr) / 400.0f;
        return 1.0f / (1.0f + MathF.Pow(10.0f, exponent));
    }

    /// <summary>Writes mmrAfter[i] for each fighter. Fighters with rateMask[i]==false are left unchanged (mmrAfter = mmrBefore).
    /// Stopped/Draw: no changes. winsDelta/lossesDelta: 1 when that fighter is rated and wins/loses (else 0); either may be null.
    /// When BalanceTuning.Get().ZeroSumRating is set, deltas are weighted by the rated team sizes and then rescaled so the bout's
    /// total rating change is zero. Without it each fighter moves by an independent delta.</summary>
    // C++: MmrRating.cpp:150-264
    public static void ApplyMatch(
        SparPodium.Snapshot snap,
        float[]? mmrBefore,
        int[]? matchesBefore,
        bool[]? rateMask,
        float[]? mmrAfter,
        int[]? winsDelta,
        int[]? lossesDelta)
    {
        if (mmrBefore == null || matchesBefore == null || rateMask == null || mmrAfter == null)
            return;

        for (int i = 0; i < snap.FighterCount; ++i)
        {
            mmrAfter[i] = mmrBefore[i];
            if (winsDelta != null)
                winsDelta[i] = 0;
            if (lossesDelta != null)
                lossesDelta[i] = 0;
        }

        if (!ShouldRate(snap.Outcome) || snap.FighterCount <= 0)
            return;

        float[] perfMult = new float[16];
        ComputePerfMultipliers(snap.Fighters, snap.FighterCount, perfMult);

        bool zeroSum = BalanceTuning.Get().ZeroSumRating;

        // Pass 1 - raw delta per rated fighter, weighted by rated team sizes.
        float[] pending = new float[16];
        for (int i = 0; i < snap.FighterCount; ++i)
        {
            pending[i] = 0.0f;
            if (!rateMask[i])
                continue;

            float S = FighterWon(snap, snap.Fighters[i]) ? 1.0f : 0.0f;
            float oppAvg = OpponentAverage(snap, i, mmrBefore);
            float E = ExpectedScore(mmrBefore[i], oppAvg);
            float k = (float)KFactor(matchesBefore[i]);
            // Strong performance amplifies a win, but protects a defeated fighter from an equally large loss. The [0.75, 1.25] range
            // is mirrored around 1.0 for losses.
            float resultMult = S >= 0.5f
                ? perfMult[i]
                : 2.0f - perfMult[i];
            float delta = k * (S - E) * resultMult;
            delta = Clamp(delta, -MaxAbsDelta, MaxAbsDelta);

            if (zeroSum)
            {
                // Each member's share of the result is diluted by their own team size and amplified by the opposing one. For equal
                // teams this is exactly 1.0, so 1v1 behaviour is untouched.
                int own = 1, opp = 0;
                TeamSizes(snap, i, rateMask, out own, out opp);
                if (opp > 0)
                    delta *= (2.0f * opp) / (float)(own + opp);
            }
            pending[i] = delta;
        }

        // Pass 2 - remove the residual. The weights alone are only exactly zero-sum when E and the performance multiplier are
        // uniform, so rescale the losing side to make the bout conserve rating in every case.
        if (zeroSum)
        {
            float gain = 0.0f, loss = 0.0f;
            for (int i = 0; i < snap.FighterCount; ++i)
            {
                if (!rateMask[i])
                    continue;
                if (pending[i] > 0.0f)
                    gain += pending[i];
                else if (pending[i] < 0.0f)
                    loss -= pending[i];
            }
            if (gain > 0.0001f && loss > 0.0001f && MathF.Abs(gain - loss) > 0.0001f)
            {
                float scale = gain / loss;
                for (int i = 0; i < snap.FighterCount; ++i)
                    if (rateMask[i] && pending[i] < 0.0f)
                        pending[i] *= scale;
            }
        }

        // Pass 3 - apply, clamp, write.
        for (int i = 0; i < snap.FighterCount; ++i)
        {
            if (!rateMask[i])
                continue;

            float S = FighterWon(snap, snap.Fighters[i]) ? 1.0f : 0.0f;
            float delta = Clamp(pending[i], -MaxAbsDelta, MaxAbsDelta);

            float next = mmrBefore[i] + delta;
            if (next < FloorMmr)
                next = FloorMmr;
            mmrAfter[i] = next;

            if (S >= 0.5f)
            {
                if (winsDelta != null)
                    winsDelta[i] = 1;
            }
            else
            {
                if (lossesDelta != null)
                    lossesDelta[i] = 1;
            }
        }
    }
}
