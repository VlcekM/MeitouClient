using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Bodies;

/// <summary>
/// The races a special food (<c>item function</c> 15) is for: the "-Only for:" row of its tooltip (docs/game/ui-screens.md "Item tooltip").
/// </summary>
public static class SpecialFood
{
    /// <summary>
    /// The longest the running text may be before a name that would pass it starts a new line (the original compares with 0x19).
    /// </summary>
    const int LineBreakAfter = 25;

    /// <summary>
    /// The row's value (<b>Verified</b>, <c>FUN_1407ae130</c> after the function-15 label): the RACE records whose <c>special food</c> list holds
    /// <paramref name="item"/>, in the database's order (<b>Observed</b>: the original walks its record store; Meitou walks the loaded order). Each
    /// race is named, except that a race group all of whose races are listed is named once, at its first listed race. Names are joined by ", " and
    /// one line break is inserted before the first name that would take the text past 25 characters. Empty when no race eats it.
    /// </summary>
    public static string EatenBy(GameDatabase db, string item)
    {
        // a race listed by several groups keeps the last (the race loader)
        var groupOf = new Dictionary<string, GameRecord>(StringComparer.Ordinal);
        foreach (var group in db.OfType(FcsRecordType.RaceGroup))
            foreach (var race in group.GetReferences("races")) groupOf[race.TargetStringId] = group;

        var eaters = db.OfType(FcsRecordType.Race).Where(r => r.GetReferences("special food").Any(f => f.TargetStringId == item)).ToList();
        var listed = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var race in eaters)
            if (groupOf.TryGetValue(race.StringId, out var group))
                listed[group.StringId] = listed.GetValueOrDefault(group.StringId) + 1;

        var text = new System.Text.StringBuilder();
        bool mayBreak = true;
        foreach (var race in eaters)
        {
            string name = race.Name;
            if (groupOf.TryGetValue(race.StringId, out var group) && listed[group.StringId] is var count && count != 0)
            {
                if (count == group.GetReferences("races").Count)
                {
                    listed[group.StringId] = 0;
                    name = group.Name;
                }
            }
            else if (group is not null) continue;

            if (text.Length > 0) text.Append(", ");
            if (mayBreak && text.Length > 0 && text.Length + name.Length > LineBreakAfter)
            {
                text.Append('\n');
                mayBreak = false;
            }

            text.Append(name);
        }

        return text.ToString();
    }
}
