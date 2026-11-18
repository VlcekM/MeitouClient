namespace ProvingGrounds.Core;

// Session-only UI lineup. IDs are resolved against the current player roster.
/// <summary>C++: TownRosterSelection.h (header-only, namespace <c>TownRosterSelection</c>). <c>std::vector&lt;std::string&gt;&amp;</c> is a
/// <see cref="List{T}"/> mutated in place; ids compare with ordinal string equality (std::string ==).</summary>
public static class TownRosterSelection
{
    // C++: TownRosterSelection.h:9-15
    public static bool Add(List<string> team, string id, bool ready, bool locked)
    {
        if (locked || !ready || id.Length == 0 || team.Count >= 3 ||
            team.Contains(id, StringComparer.Ordinal)) return false;
        team.Add(id);
        return true;
    }

    // C++: TownRosterSelection.h:16-23
    public static bool Remove(List<string> team, string id, bool locked)
    {
        if (locked) return false;
        int found = team.FindIndex(t => string.Equals(t, id, StringComparison.Ordinal));
        if (found < 0) return false;
        team.RemoveAt(found);
        return true;
    }

    // C++: TownRosterSelection.h:24-29
    public static void Prune(List<string> team, List<string> roster)
    {
        for (int i = 0; i < team.Count; )
            if (!roster.Contains(team[i], StringComparer.Ordinal)) team.RemoveAt(i);
            else ++i;
    }

    // C++: TownRosterSelection.h:30-34
    public static bool Toggle(List<string> team, string id, bool ready, bool locked)
    {
        if (team.Contains(id, StringComparer.Ordinal)) return Remove(team, id, locked);
        return Add(team, id, ready, locked);
    }
}
