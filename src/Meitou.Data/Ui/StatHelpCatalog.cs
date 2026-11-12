using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Data.Ui;

/// <summary>
/// Stat help read from the user's English gettext template, never embedded game text; shown in the active language when its catalog translates the
/// paragraph.
/// </summary>
public sealed class StatHelpCatalog
{
    /// <summary>
    /// A stat paragraph of the template: the <c>statswindow.cpp</c> lines that reference it, its text in the active language and the length of its
    /// English msgid.
    /// </summary>
    sealed record Entry(int[] Lines, string Text, int EnglishLength);
    /// <summary>
    /// Every template entry referenced from <c>kenshi/statswindow.cpp</c>, in template order.
    /// </summary>
    readonly List<Entry> _entries = [];
    /// <summary>
    /// The help of one stat: its description, the lines naming what it affects and how it trains, and its physical effect ("" when none).
    /// </summary>
    public sealed record Help(string Description, IReadOnlyList<string> Affected, IReadOnlyList<string> Training, string PhysicalEffect);

    /// <summary>
    /// The text referenced from source line <paramref name="line"/>, the longest English msgid when several share it; "" when none.
    /// </summary>
    public string TextAt(int line) => _entries.Where(e => e.Lines.Contains(line)).MaxBy(e => e.EnglishLength)?.Text ?? "";

    /// <summary>
    /// <see cref="From"/> the gettext template at <paramref name="path"/>.
    /// </summary>
    public static StatHelpCatalog Read(string? path) => From(PotTemplate.Read(path));

    /// <summary>
    /// The catalog of the template's entries referenced from <c>kenshi/statswindow.cpp</c>.
    /// </summary>
    public static StatHelpCatalog From(IEnumerable<PotEntry> template)
    {
        var result = new StatHelpCatalog();
        // The structure (which paragraph answers which stat) comes from the English template; the text shown is its translation when the language
        // has one.
        foreach (var entry in template)
            if (entry.References.Where(r => r.File == "kenshi/statswindow.cpp").Select(r => r.Line).Distinct().ToArray() is { Length: > 0 } lines)
                result._entries.Add(new(lines, Loc.Tr(entry.Id), entry.Id.Length));
        return result;
    }

    /// <summary>
    /// The help of <paramref name="stat"/>, from the source lines of its paragraphs in the 1.0.68 template; empty texts for a stat without help.
    /// </summary>
    public Help For(StatsEnumerated stat)
    {
        // Source references in the installed 1.0.68 main.pot identify stat paragraphs and help groups.
        // These are catalog metadata, not a translation of the original program's logic.
        var (description, affected, training, end, physical) = stat switch
        {
            StatsEnumerated.Strength => (937, 499, 548, 553, 429),
            StatsEnumerated.Toughness => (942, 554, 568, 578, 433),
            StatsEnumerated.Dexterity => (945, 579, 587, 591, 422),
            StatsEnumerated.Perception => (947, 709, 714, 718, 0),
            StatsEnumerated.MeleeAttack => (959, 592, 597, 604, 0),
            StatsEnumerated.MeleeDefence => (960, 605, 609, 616, 0),
            StatsEnumerated.MartialArts => (961, 617, 625, 633, 426),
            StatsEnumerated.Dodge => (963, 634, 641, 646, 0),
            StatsEnumerated.Athletics => (973, 647, 654, 658, 0),
            StatsEnumerated.Swimming => (974, 659, 666, 670, 0),
            StatsEnumerated.Medic => (979, 671, 675, 680, 0),
            StatsEnumerated.Robotics => (984, 671, 675, 680, 0),
            StatsEnumerated.Turrets => (956, 689, 694, 698, 0),
            StatsEnumerated.Crossbows => (957, 699, 704, 708, 0),
            StatsEnumerated.FriendlyFire => (964, 719, 722, 726, 0),
            StatsEnumerated.Thieving => (968, 727, 732, 736, 0),
            StatsEnumerated.Lockpicking => (967, 737, 741, 746, 0),
            StatsEnumerated.Stealth => (966, 747, 759, 763, 0),
            StatsEnumerated.Assassination => (970, 764, 773, 778, 0),
            StatsEnumerated.Engineering => (980, 779, 784, 789, 0),
            StatsEnumerated.Science => (985, 790, 794, 800, 437),
            StatsEnumerated.Labouring => (987, 801, 806, 810, 0),
            StatsEnumerated.Farming => (988, 811, 817, 821, 0),
            StatsEnumerated.Cooking => (989, 822, 826, 831, 0),
            StatsEnumerated.SmithingWeapon => (981, 832, 839, 843, 0),
            StatsEnumerated.SmithingArmour => (982, 844, 853, 857, 0),
            StatsEnumerated.SmithingBow => (983, 858, 868, 870, 0),
            StatsEnumerated.Katanas => (950, 681, 684, 688, 0),
            StatsEnumerated.Sabres => (951, 681, 684, 688, 0),
            StatsEnumerated.Hackers => (952, 681, 684, 688, 0),
            StatsEnumerated.HeavyWeapons => (953, 681, 684, 688, 0),
            StatsEnumerated.Blunt => (954, 681, 684, 688, 0),
            StatsEnumerated.Polearms => (955, 681, 684, 688, 0),
            _ => (0, 0, 0, 0, 0),
        };
        string[] Between(int from, int to) => _entries.SelectMany(e => e.Lines.Where(l => l > from && l < to).Select(l => (Line: l, e.Text)))
            .OrderBy(e => e.Line).Select(e => e.Text).Distinct().ToArray();
        return new(TextAt(description), Between(affected, training), Between(training, end + 1), TextAt(physical));
    }
}