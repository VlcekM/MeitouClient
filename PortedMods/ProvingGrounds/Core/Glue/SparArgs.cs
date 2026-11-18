namespace ProvingGrounds.Core.Glue;

/// <summary>
/// The arguments of the TEST-ONLY <c>pg spar &lt;avb|1v1|ffa&gt; &lt;idA...&gt; [vs &lt;idB...&gt;]</c> (task 3.3; task 3.5 keeps the verb), mapped as
/// the original's Arena window builds its start (ArenaUI.cpp:2388-2430): its three mode buttons UiAvB / UiTeams1v1 / UiLastStanding give
/// <c>avb</c> = <see cref="MatchRules.MatchMode.TeamAvB"/>, <c>1v1</c> = <see cref="MatchRules.MatchMode.Teams1v1"/> (one fighter of each team at a
/// time, the rest on the bench) and <c>ffa</c> = <see cref="MatchRules.MatchMode.LastStanding"/>. The team modes list team A (TeamA) then team B
/// (TeamB) after <c>vs</c>, as the window lists g_teamA then g_teamB; Last Standing lists the pool, each with TeamNone, and takes no <c>vs</c>.
/// Nothing else is checked here: StartMatch's own refusals (count, duplicates, conscious, teams) answer the rest with the C++ status texts.
/// </summary>
public static class SparArgs
{
    public delegate bool TryParseId<TId>(string text, out TId id);

    public const string Usage = "usage pg spar <avb|1v1|ffa> <idA...> [vs <idB...>] (ids c:<slot>.<generation>; avb and 1v1 need vs)";

    /// <summary>The mode word (lower case) or false.</summary>
    public static bool TryMode(string word, out MatchRules.MatchMode mode)
    {
        switch (word)
        {
            case "avb": mode = MatchRules.MatchMode.TeamAvB; return true;
            case "1v1": mode = MatchRules.MatchMode.Teams1v1; return true;
            case "ffa": mode = MatchRules.MatchMode.LastStanding; return true;
            default: mode = default; return false;
        }
    }

    /// <summary><paramref name="args"/> = the words after <c>spar</c>. False with <paramref name="error"/> (the usage) for an unknown mode, an id
    /// that does not parse, a missing or doubled <c>vs</c>, an empty side, or a <c>vs</c> in Last Standing.</summary>
    public static bool TryParse<TId>(IReadOnlyList<string> args, TryParseId<TId> parseId, out MatchRules.MatchMode mode, out TId[] fighters,
        out MatchRules.MatchTeam[] teams, out string error)
    {
        fighters = [];
        teams = [];
        error = Usage;
        if (args.Count < 1 || !TryMode(args[0].ToLowerInvariant(), out mode)) { mode = default; return false; }
        bool teamMode = mode != MatchRules.MatchMode.LastStanding;
        var ids = new List<TId>();
        var sides = new List<MatchRules.MatchTeam>();
        int vsCount = 0;
        for (int i = 1; i < args.Count; i++)
        {
            if (string.Equals(args[i], "vs", StringComparison.OrdinalIgnoreCase)) { vsCount++; continue; }
            if (!parseId(args[i], out TId id)) return false;
            ids.Add(id);
            sides.Add(!teamMode ? MatchRules.MatchTeam.None : vsCount == 0 ? MatchRules.MatchTeam.TeamA : MatchRules.MatchTeam.TeamB);
        }
        if (teamMode ? vsCount != 1 || !sides.Contains(MatchRules.MatchTeam.TeamA) || !sides.Contains(MatchRules.MatchTeam.TeamB) : vsCount != 0)
            return false;
        fighters = [.. ids];
        teams = [.. sides];
        error = "";
        return true;
    }
}
