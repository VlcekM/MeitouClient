namespace ProvingGrounds.Core;

/// <summary>Match modes, who fights whom, and when a bout ends. C++: MatchRules.h, MatchRules.cpp (PA §2.4).</summary>
public static class MatchRules
{
    public enum MatchMode { TeamAvB = 0, Teams1v1 = 1, LastStanding = 2 }
    public enum MatchTeam { None = 0, TeamA = 1, TeamB = 2 }
    public enum MatchEndKind { None = 0, TeamWipeA = 1, TeamWipeB = 2, LastStanding = 3, Draw = 4 }

    // Discriminating label for the log. CombatBalanceLog used to record only
    // SparSession::StopReason, which routes EVERY terminal outcome through the
    // KO branch, so a team wipe and a mutual knockout were indistinguishable.
    // C++: MatchRules.h:15-23
    public static string EndKindName(MatchEndKind kind)
    {
        switch (kind)
        {
            case MatchEndKind.TeamWipeA: return "team_wipe_a";
            case MatchEndKind.TeamWipeB: return "team_wipe_b";
            case MatchEndKind.LastStanding: return "last_standing";
            case MatchEndKind.Draw: return "draw";
            default: return "none";
        }
    }

    /// <summary>C++: MatchRules.h:25-33 (plain struct, copied by value).</summary>
    public struct MatchParticipant
    {
        public int Id;
        public MatchTeam Team;
        public bool Conscious; // false if KO or dead for "conscious" checks
        public bool Dead;
        // Teams 1v1: once KO'd in a bout, stays out even if they wake up.
        public bool Eliminated;
    }

    // C++: MatchRules.cpp:5-12
    public static bool IsOpponent(MatchMode mode, in MatchParticipant a, in MatchParticipant b)
    {
        if (a.Id == b.Id)
            return false;
        if (mode == MatchMode.TeamAvB || mode == MatchMode.Teams1v1)
            return a.Team != MatchTeam.None && b.Team != MatchTeam.None && a.Team != b.Team;
        return true; // LastStanding
    }

    // Returns true when a team counts as defeated. downedPercent 0 requires every
    // member to be down (shipped behaviour); a positive value ends the team once
    // that percentage is down.
    // C++: MatchRules.cpp:17-38
    static bool TeamDown(MatchParticipant[] list, int count, MatchTeam team, bool honorEliminated, int downedPercent)
    {
        int total = 0;
        int down = 0;
        for (int i = 0; i < count; ++i)
        {
            if (list[i].Team != team)
                continue;
            ++total;
            bool up = list[i].Conscious && !(honorEliminated && list[i].Eliminated);
            if (!up)
                ++down;
        }
        if (total <= 0)
            return false;
        if (downedPercent <= 0)
            return down == total;
        // Integer comparison, so a 1-fighter team ends at down == 1 for any
        // threshold and a 4-fighter team at 50% ends at down == 2.
        return down * 100 >= downedPercent * total;
    }

    /// <summary>downedPercent 0 requires an entire team to be down (shipped behaviour). A positive value ends a team bout as soon as
    /// that percentage of the team is down, which bounds the injury load a single bout can inflict. A null list (the C++ null
    /// pointer) or count &lt;= 0 is <see cref="MatchEndKind.None"/>; the first <paramref name="count"/> entries are read.</summary>
    // C++: MatchRules.cpp:40-72
    public static MatchEndKind EvaluateEnd(MatchMode mode, MatchParticipant[]? list, int count, int downedPercent = 0)
    {
        if (list == null || count <= 0)
            return MatchEndKind.None;

        if (mode == MatchMode.TeamAvB || mode == MatchMode.Teams1v1)
        {
            bool honorEliminated = (mode == MatchMode.Teams1v1);
            bool aDown = TeamDown(list, count, MatchTeam.TeamA, honorEliminated, downedPercent);
            bool bDown = TeamDown(list, count, MatchTeam.TeamB, honorEliminated, downedPercent);
            if (aDown && bDown)
                return MatchEndKind.Draw;
            if (aDown)
                return MatchEndKind.TeamWipeA;
            if (bDown)
                return MatchEndKind.TeamWipeB;
            return MatchEndKind.None;
        }

        // LastStanding
        int awake = 0;
        for (int i = 0; i < count; ++i)
        {
            if (list[i].Conscious)
                ++awake;
        }
        if (awake <= 0)
            return MatchEndKind.Draw;
        if (awake == 1)
            return MatchEndKind.LastStanding;
        return MatchEndKind.None;
    }
}
