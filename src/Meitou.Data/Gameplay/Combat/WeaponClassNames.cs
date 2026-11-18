namespace Meitou.Data.Gameplay.Combat;

/// <summary>
/// Weapon classes a data mod names (docs/modding.md "Weapon types", Meitou only): a WEAPON and the COMBAT_TECHNIQUE records with the same
/// <c>meitou class</c> text form a class whose weapons pick their attacks, blocks and dodges from those techniques alone, as a creature's weapon
/// does from its own (<c>animal</c> 9 and up, docs/game/combat.md). Each name gets a category number of its own, past every number the game or
/// its data uses, in the order the merged data first names it (weapons first); the numbers are not saved.
/// </summary>
public static class WeaponClassNames
{
    /// <summary>
    /// The category number of the first class named.
    /// </summary>
    public const int FirstCategory = 1_000_000;

    /// <summary>
    /// Gives every weapon and technique of <paramref name="weapons"/> and <paramref name="techniques"/> that names a class its class's number (a
    /// weapon's <see cref="WeaponData.ClassCategory"/>, a technique's <see cref="CombatTechnique.Animal"/>), in place, and logs each class that
    /// cannot work as meant (<see cref="WarnIncomplete"/>). Names are trimmed when read and match ignoring case. Nothing is made when no record
    /// names one.
    /// </summary>
    public static void Apply(List<WeaponData> weapons, List<CombatTechnique> techniques)
    {
        Dictionary<string, int>? numbers = null;
        for (int i = 0; i < weapons.Count; i++)
            if (weapons[i].ModClass is { } name) weapons[i] = weapons[i] with { ClassCategory = NumberOf(ref numbers, name) };
        for (int i = 0; i < techniques.Count; i++)
            if (techniques[i].ModClass is { } name) techniques[i] = techniques[i] with { Animal = NumberOf(ref numbers, name) };

        if (numbers is not null) WarnIncomplete(numbers, weapons, techniques);
    }

    /// <summary>
    /// One log line per class, at load, for a class named only by techniques (never used), only by weapons (their wielders kick and punch and
    /// never block or dodge) or with no attack technique (its weapons kick and punch): usually a misspelled name.
    /// </summary>
    static void WarnIncomplete(Dictionary<string, int> numbers, List<WeaponData> weapons, List<CombatTechnique> techniques)
    {
        foreach (var (name, number) in numbers)
        {
            bool armed = weapons.Exists(w => w.ClassCategory == number);
            bool moves = techniques.Exists(t => t.Animal == number);
            bool attacks = techniques.Exists(t => t.Animal == number && !t.Disabled && !t.IsBlock && !t.IsDodge);
            string? problem = !armed ? "only techniques name it, so they are never used"
                : !moves ? "no technique names it, so its weapons only kick and punch and never block or dodge (but out of a stumble)"
                : !attacks ? "it has no attack technique, so its weapons only kick and punch"
                : null;
            if (problem is not null) Console.WriteLine($"warning   weapon class '{name}' (meitou class): {problem}");
        }
    }

    /// <summary>
    /// The number of class <paramref name="name"/>, a new one after the others when it is not named yet.
    /// </summary>
    static int NumberOf(ref Dictionary<string, int>? numbers, string name)
    {
        numbers ??= new(StringComparer.OrdinalIgnoreCase);
        if (numbers.TryGetValue(name, out int number)) return number;

        number = FirstCategory + numbers.Count;
        numbers[name] = number;
        return number;
    }
}
