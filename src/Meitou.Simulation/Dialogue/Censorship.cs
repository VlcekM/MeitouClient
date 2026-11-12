namespace Meitou.Simulation.Dialogue;

/// <summary>
/// The optional text censorship (settings.cfg <c>censorship</c>, <c>FUN_14067e8b0</c>, docs/game/dialogue.md "Censorship"): a fixed list of
/// replacements applied in order, each replacing every occurrence (case-sensitive, so the lower, Title and UPPER case are separate entries; the
/// original has no UPPER entries for shitting, shitty and asswipe).
/// </summary>
public static class Censorship
{
    /// <summary>
    /// The replacements, applied in this order.
    /// </summary>
    static readonly (string From, string To)[] Pairs =
    [
        ("shitting", "crapping"), ("Shitting", "Crapping"), ("shitty", "crappy"), ("Shitty", "Crappy"),
        ("shit", "crap"), ("Shit", "Crap"), ("SHIT", "CRAP"),
        ("fuck", "bugger"), ("FUCK", "BUGGER"), ("Fuck", "Bugger"),
        ("Bollocks", "Damn"), ("bollocks", "damn"), ("BOLLOCKS", "DAMN"),
        ("asshole", "idiot"), ("Asshole", "Idiot"), ("ASSHOLE", "IDIOT"), ("asswipe", "idiot"),
    ];

    /// <summary>
    /// <paramref name="text"/> with every pair's replacement applied in order.
    /// </summary>
    public static string Apply(string text)
    {
        foreach (var (from, to) in Pairs)
            text = text.Replace(from, to, StringComparison.Ordinal);

        return text;
    }
}
