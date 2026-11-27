using Meitou.Data.Gameplay.Construction;

namespace Meitou.Game.Building;

/// <summary>
/// What the build menu's search box looks through: the category names, the building (group) names and the subtypes a group's arrows cycle through.
/// </summary>
[Flags]
public enum BuildSearchScope
{
    /// <summary>
    /// Nothing: the search shows no results.
    /// </summary>
    None = 0,
    /// <summary>
    /// The categories list's names: a matching category lists all its buildings.
    /// </summary>
    Categories = 1,
    /// <summary>
    /// The buildings list's names.
    /// </summary>
    Buildings = 2,
    /// <summary>
    /// The members of a group of more than one, which the window's two arrows cycle through.
    /// </summary>
    Subtypes = 4,
    /// <summary>
    /// Categories, buildings and subtypes.
    /// </summary>
    All = Categories | Buildings | Subtypes,
}

/// <summary>
/// Meitou's search over the build menu (docs/game/ui-screens.md "Build menu search"): the query's words must all appear, ignoring case, in a shown
/// name. A category shows when its name matches or one of its groups does; inside it a group shows when the category matched, its name matches or
/// one of its members does; a group's matching members come first (its default pick and the right arrow's first steps), the rest keep their order.
/// The names are the ones the window shows, given by the caller.
/// </summary>
public sealed record BuildMenuSearch(string Query, BuildSearchScope Scope)
{
    /// <summary>
    /// No query: the menu as it is.
    /// </summary>
    public static readonly BuildMenuSearch Empty = new("", BuildSearchScope.All);

    /// <summary>
    /// The query's words (computed from <see cref="Query"/> each time: a <c>with</c> copy that changes the query keeps no stale words).
    /// </summary>
    string[] Words => Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Whether the search filters the menu: the query holds more than spaces.
    /// </summary>
    public bool Active => Words.Length > 0;

    /// <summary>
    /// Whether <paramref name="text"/> holds every word of the query, ignoring case.
    /// </summary>
    public bool Matches(string text) => Words.All(word => text.Contains(word, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>
    /// Whether category <paramref name="name"/> matches by its own name (with <see cref="BuildSearchScope.Categories"/> on).
    /// </summary>
    public bool CategoryHit(string name) => Scope.HasFlag(BuildSearchScope.Categories) && Matches(name);

    /// <summary>
    /// The categories that show: each whose name matches or that keeps a group (<see cref="Groups"/>); all of them without a query.
    /// </summary>
    public IReadOnlyList<string> Categories(IReadOnlyList<string> categories, Func<string, string> categoryName,
        Func<string, IReadOnlyList<BuildGroup>> groupsOf, Func<BuildGroup, string> groupName, Func<BuildingBuildData, string> memberName)
    {
        if (!Active) return categories;

        return [.. categories.Where(c => CategoryHit(categoryName(c)) || Groups(groupsOf(c), false, groupName, memberName).Count > 0)];
    }

    /// <summary>
    /// The groups that show of a category (<paramref name="categoryHit"/>: its name matched, so all of them), each with its matching members
    /// first; the groups as they are without a query.
    /// </summary>
    public IReadOnlyList<BuildGroup> Groups(IReadOnlyList<BuildGroup> groups, bool categoryHit, Func<BuildGroup, string> groupName,
        Func<BuildingBuildData, string> memberName)
    {
        if (!Active) return groups;

        var shown = new List<BuildGroup>();
        foreach (var group in groups)
        {
            var hits = MemberHits(group, memberName);
            bool groupHit = Scope.HasFlag(BuildSearchScope.Buildings) && Matches(groupName(group));
            if (!categoryHit && !groupHit && hits.Count == 0) continue;

            shown.Add(hits.Count == 0 ? group : group with { Members = [.. hits, .. group.Members.Where(m => !hits.Contains(m))] });
        }

        return shown;
    }

    /// <summary>
    /// The members of <paramref name="group"/> whose names match, in the group's order; none with <see cref="BuildSearchScope.Subtypes"/> off or
    /// for a group of one, which has no subtypes to cycle through.
    /// </summary>
    List<BuildingBuildData> MemberHits(BuildGroup group, Func<BuildingBuildData, string> memberName) =>
        Scope.HasFlag(BuildSearchScope.Subtypes) && group.Members.Count > 1 ? [.. group.Members.Where(m => Matches(memberName(m)))] : [];
}
