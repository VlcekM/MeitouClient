// Port of the pure target choice of upstream/ProvingGrounds/plugin/src/FightStarter.cpp (Proving Grounds by Shutori, GPLv3), task 3.1.
// The C++ functions read Character pointers; here each fighter is a plain value (whether the pointer is set, isValid, isDead, isUnconcious,
// getPosition) so the choice needs no game. The glue (src/Game/FightStarter.cs) fills the values from the engine adapter.
namespace ProvingGrounds.Core;

/// <summary>Which opponent a sparring fighter is ordered at (PA §2.7). C++: FightStarter.cpp:132-135, 176-277 (anonymous namespace).</summary>
public static class FightTargeting
{
    /// <summary>One fighter as the C++ reads it through its <c>Character*</c>.</summary>
    public struct Fighter
    {
        /// <summary>The pointer is not null.</summary>
        public bool Present;
        /// <summary><c>isValid()</c>.</summary>
        public bool Valid;
        /// <summary><c>isDead()</c>.</summary>
        public bool Dead;
        /// <summary><c>isUnconcious()</c>.</summary>
        public bool Unconscious;
        /// <summary><c>getPosition().x</c>, <c>.y</c>, <c>.z</c>.</summary>
        public float X, Y, Z;
    }

    // C++: FightStarter.cpp:132-135
    public static bool IsLivingFighter(in Fighter c) => c.Present && c.Valid && !c.Dead && !c.Unconscious;

    // C++: FightStarter.cpp:176-184
    public static MatchRules.MatchParticipant MakeParticipant(int index, MatchRules.MatchTeam team, in Fighter c)
    {
        MatchRules.MatchParticipant p = default;
        p.Id = index;
        p.Team = team;
        p.Conscious = IsLivingFighter(c);
        p.Dead = c.Present && c.Dead;
        return p;
    }

    // C++: FightStarter.cpp:186-203
    public static bool IsLivingOpponent(MatchRules.MatchMode mode, int attackerIdx, int targetIdx, ReadOnlySpan<Fighter> fighters,
        ReadOnlySpan<MatchRules.MatchTeam> teams)
    {
        if (attackerIdx == targetIdx)
            return false;

        ref readonly Fighter target = ref fighters[targetIdx];
        if (!IsLivingFighter(target))
            return false;

        MatchRules.MatchParticipant attacker = MakeParticipant(attackerIdx, teams[attackerIdx], fighters[attackerIdx]);
        MatchRules.MatchParticipant opponent = MakeParticipant(targetIdx, teams[targetIdx], target);
        return MatchRules.IsOpponent(mode, attacker, opponent);
    }

    // C++: FightStarter.cpp:205-210
    public static float HorizontalDistSq(in Fighter a, in Fighter b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dz * dz);
    }

    // C++: FightStarter.cpp:212-240
    public static int FindNearestLivingOpponent(MatchRules.MatchMode mode, int attackerIdx, ReadOnlySpan<Fighter> fighters,
        ReadOnlySpan<MatchRules.MatchTeam> teams, int count)
    {
        ref readonly Fighter attacker = ref fighters[attackerIdx];
        if (!attacker.Present)
            return -1;

        int nearestIdx = -1;
        float nearestDistSq = 1.0e30f;
        for (int targetIdx = 0; targetIdx < count; ++targetIdx)
        {
            if (!IsLivingOpponent(mode, attackerIdx, targetIdx, fighters, teams))
                continue;

            float distSq = HorizontalDistSq(attacker, fighters[targetIdx]);
            if (distSq < nearestDistSq)
            {
                nearestDistSq = distSq;
                nearestIdx = targetIdx;
            }
        }
        return nearestIdx;
    }

    // C++: FightStarter.cpp:242-277
    public static int FindMatchedLivingTeamOpponent(MatchRules.MatchMode mode, int attackerIdx, ReadOnlySpan<Fighter> fighters,
        ReadOnlySpan<MatchRules.MatchTeam> teams, int count)
    {
        MatchRules.MatchTeam attackerTeam = teams[attackerIdx];
        int attackerSlot = 0;
        for (int i = 0; i < attackerIdx; ++i)
        {
            if (teams[i] == attackerTeam)
                ++attackerSlot;
        }

        int livingOpponentCount = 0;
        for (int targetIdx = 0; targetIdx < count; ++targetIdx)
        {
            if (IsLivingOpponent(mode, attackerIdx, targetIdx, fighters, teams))
                ++livingOpponentCount;
        }
        if (livingOpponentCount <= 0)
            return -1;

        int desiredSlot = attackerSlot % livingOpponentCount;
        int livingSlot = 0;
        for (int targetIdx = 0; targetIdx < count; ++targetIdx)
        {
            if (!IsLivingOpponent(mode, attackerIdx, targetIdx, fighters, teams))
                continue;
            if (livingSlot == desiredSlot)
                return targetIdx;
            ++livingSlot;
        }
        return -1;
    }

    /// <summary>The single target a fighter is ordered at: Last Standing the nearest living opponent, the two team modes the distributed one,
    /// any other mode value the first living opponent. -1: none. Callers (Core/Glue/FightStarterLogic.cs): KeepEngagedMatch for every mode;
    /// EngageMatch for Last Standing and the team modes only (its fallback for other mode values orders every living opponent, not one).</summary>
    // C++: FightStarter.cpp:498-499, 518-519 (EngageMatch's two calls) and 623-656 (KeepEngagedMatch): the dispatch, lifted into one function
    public static int Choose(MatchRules.MatchMode mode, ReadOnlySpan<Fighter> fighters, ReadOnlySpan<MatchRules.MatchTeam> teams, int count,
        int attackerIdx)
    {
        if (mode == MatchRules.MatchMode.LastStanding)
            return FindNearestLivingOpponent(mode, attackerIdx, fighters, teams, count);
        if (mode == MatchRules.MatchMode.TeamAvB || mode == MatchRules.MatchMode.Teams1v1)
            return FindMatchedLivingTeamOpponent(mode, attackerIdx, fighters, teams, count);
        for (int j = 0; j < count; ++j)
        {
            if (IsLivingOpponent(mode, attackerIdx, j, fighters, teams))
                return j;
        }
        return -1;
    }

    /// <summary>KeepEngagedMatch's "this fighter is fine" test: its current target is a living opponent and it is a player character or in
    /// melee combat mode (a non-player can keep a target after its combat task stalls).</summary>
    // C++: FightStarter.cpp:616-621
    public static bool KeepsTarget(bool validTarget, bool isPlayerCharacter, bool inMeleeCombatMode) =>
        validTarget && (isPlayerCharacter || inMeleeCombatMode);

    /// <summary>ReactToIncomingAttack's guard: true when the defender switches to the attacker.</summary>
    // C++: FightStarter.cpp:575-584
    public static bool ShouldReact(bool defenderLiving, bool attackerLiving, bool sessionActive, MatchRules.MatchMode sessionMode,
        bool defenderEliminated, bool attackerEliminated, bool sparringOpponents, bool attackerIsAssignedTarget) =>
        !(!defenderLiving || !attackerLiving ||
          !sessionActive ||
          sessionMode == MatchRules.MatchMode.Teams1v1 ||
          defenderEliminated ||
          attackerEliminated ||
          !sparringOpponents ||
          attackerIsAssignedTarget);
}
