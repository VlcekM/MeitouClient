namespace Meitou.Data.Gameplay;

/// <summary>
/// A code mod's answer to "may a wearer of this race wear this item?" (<see cref="WearRules"/>): <paramref name="item"/> is the item's record string
/// id, <paramref name="race"/> the wearer's RACE string id, <paramref name="animal"/> whether the wearer is an animal. False refuses the item.
/// </summary>
public delegate bool WearRule(string item, string race, bool animal);

/// <summary>
/// The wear rules code mods added (docs/modding.md "Wear rules"), asked after an item's own race lists pass, wherever <see cref="RaceLimiter"/>
/// is asked with the item. Filled at startup before the first world is built; empty without mods, so the race lists alone decide. Any thread
/// may ask, so a rule must be quick, read no world state and answer the same for the same arguments.
/// </summary>
public static class WearRules
{
    /// <summary>
    /// A rule with the mod that added it.
    /// </summary>
    sealed class Entry(string owner, WearRule rule)
    {
        /// <summary>
        /// The mod's id, for the log line.
        /// </summary>
        public string Owner { get; } = owner;

        /// <summary>
        /// The rule.
        /// </summary>
        public WearRule Rule { get; } = rule;

        /// <summary>
        /// 1 once a throw of this rule was logged, so a rule that keeps throwing logs once.
        /// </summary>
        public int Logged;
    }

    /// <summary>
    /// The rules in registration order; replaced whole on a change, so a reader on any thread sees a complete list.
    /// </summary>
    static volatile Entry[] s_rules = [];

    /// <summary>
    /// Serializes the changes.
    /// </summary>
    static readonly Lock Gate = new();

    /// <summary>
    /// Adds <paramref name="rule"/> for mod <paramref name="owner"/>.
    /// </summary>
    public static void Add(string owner, WearRule rule)
    {
        lock (Gate)
        {
            s_rules = [.. s_rules, new Entry(owner, rule)];
        }
    }

    /// <summary>
    /// Whether every rule lets a wearer of <paramref name="race"/> wear <paramref name="item"/>.
    /// </summary>
    public static bool Allows(string item, string race, bool animal)
    {
        foreach (var entry in s_rules)
        {
            if (!Ask(entry, item, race, animal)) return false;
        }
        return true;
    }

    /// <summary>
    /// One rule's answer. A throw counts as yes and is logged the first time; the rule stays, so the same arguments always get the same answer
    /// whichever thread asks first.
    /// </summary>
    static bool Ask(Entry entry, string item, string race, bool animal)
    {
        try
        {
            return entry.Rule(item, race, animal);
        }
        catch (Exception e)
        {
            if (Interlocked.Exchange(ref entry.Logged, 1) == 0)
                Console.Error.WriteLine($"mod       {entry.Owner}: wear rule threw (counted as yes): {e.GetType().Name}: {e.Message}");

            Content.CodeModFaults.Tell(entry.Owner, e);
            return true;
        }
    }
}
