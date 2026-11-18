using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// The stats window's skill columns a code mod's skill can join, named after each column's first heading; the skill goes at the column's foot,
/// under its last heading (Weapons, Ranged, Athletics, Trades).
/// </summary>
public enum SkillColumn
{
    /// <summary>
    /// The first skill column: the weapon skills.
    /// </summary>
    Weapons = 1,
    /// <summary>
    /// The second: the combat skills, then the ranged ones.
    /// </summary>
    Combat = 2,
    /// <summary>
    /// The third: thievery, then athletics.
    /// </summary>
    Thievery = 3,
    /// <summary>
    /// The fourth: the sciences, then the trades.
    /// </summary>
    Sciences = 4,
}

/// <summary>
/// A skill a code mod added (docs/modding.md "Skills"): its id (the save's key), its name and place on the stats window, its description pane's
/// texts, and each character's level, kept in its <see cref="CharacterStats.ModSkills"/> and trained on the game's curve.
/// </summary>
/// <param name="Id">
/// The skill's stable id: 1 to 48 letters, digits, '_', '.' or '-'; saved as the STATS key <c>meitou skill &lt;id&gt;</c>.
/// </param>
/// <param name="Label">
/// The skill's name on the stats window.
/// </param>
/// <param name="Column">
/// The column it is listed in.
/// </param>
public sealed record ModSkill(string Id, string Label, SkillColumn Column)
{
    /// <summary>
    /// The description pane's text.
    /// </summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// The description pane's "Affects" lines.
    /// </summary>
    public IReadOnlyList<string> Affects { get; init; } = [];

    /// <summary>
    /// The description pane's "Training" lines.
    /// </summary>
    public IReadOnlyList<string> Training { get; init; } = [];

    /// <summary>
    /// The level of a character that was never given one.
    /// </summary>
    public float Start { get; init; } = 1;

    /// <summary>
    /// The stat number the skill answers to once added (<see cref="ModSkills.FirstStat"/> up, in registration order;
    /// <see cref="StatsEnumerated.None"/> before): <see cref="CharacterStats"/>' indexer, <c>Stored</c> and <c>Gain</c> read and train the skill under it, and a weapon naming the
    /// skill (WEAPON <c>meitou skill</c>) uses it as its skill. Not saved: it follows the mods' load order, so keep the id, not the number.
    /// </summary>
    public StatsEnumerated Stat { get; internal set; }

    /// <summary>
    /// <paramref name="stats"/>' level in the skill.
    /// </summary>
    public float Level(CharacterStats stats) => stats.ModSkillsOrNull?.Get(Id) ?? Start;

    /// <summary>
    /// Sets <paramref name="stats"/>' level in the skill; from the mod's system in the tick (the character's own partition in a parallel phase, where
    /// no other character's levels may be read).
    /// </summary>
    public void Set(CharacterStats stats, float level) => stats.ModSkills.Set(Id, level);

    /// <summary>
    /// Trains the skill by <paramref name="amount"/> on the game's curve (<see cref="CharacterStats.CoreGain"/>: full rate at 1, slower towards 101);
    /// returns the new level. A NaN level stays NaN, as a vanilla stat's does. The character's XP rate applies as to a vanilla stat
    /// (<see cref="CharacterStats.XpRate"/>).
    /// </summary>
    public float Gain(CharacterStats stats, float amount) => Gain(stats, amount, CharacterStats.XpCap);

    /// <summary>
    /// <see cref="Gain(CharacterStats, float)"/> with the curve's cap (<see cref="CharacterStats.Gain"/> passes its own).
    /// </summary>
    internal float Gain(CharacterStats stats, float amount, float cap)
    {
        float level = Level(stats);
        if (float.IsNaN(level)) return level;

        float next = CharacterStats.CoreGain(level, stats.Rated(level, amount, cap), cap);
        Set(stats, next);
        return next;
    }
}

/// <summary>
/// The skills code mods added, in registration order. Filled at startup before the first world is built and never changed while one runs; empty
/// without mods, so the stats window and saves stay the game's.
/// </summary>
public static class ModSkills
{
    /// <summary>
    /// The stat number of the first skill added (<see cref="ModSkill.Stat"/>), past every number the game or its data uses (0 to 38).
    /// </summary>
    public const int FirstStat = 1000;

    /// <summary>
    /// The skills in registration order.
    /// </summary>
    static readonly List<ModSkill> Skills = [];

    /// <summary>
    /// The skills in registration order.
    /// </summary>
    public static IReadOnlyList<ModSkill> All => Skills;

    /// <summary>
    /// Why <paramref name="skill"/> cannot be added after <paramref name="pending"/> more of the registering mod: a bad id, label or column, or an id
    /// another skill has (case-insensitive); null when it can. The column's room is the stats window's to judge.
    /// </summary>
    public static string? Refusal(ModSkill skill, IEnumerable<ModSkill> pending)
    {
        if (skill.Id.Length is 0 or > 48 || !skill.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
            return $"Skill id '{skill.Id}' must be 1 to 48 letters, digits, '_', '.' or '-'.";
        if (string.IsNullOrWhiteSpace(skill.Label) || skill.Label.Length > 32) return "A skill's label is 1 to 32 characters.";
        if (!Enum.IsDefined(skill.Column)) return $"Skill column {(int)skill.Column} is not a column of the stats window.";
        if (Skills.Concat(pending).Any(s => string.Equals(s.Id, skill.Id, StringComparison.OrdinalIgnoreCase)))
            return $"Skill id '{skill.Id}' is taken.";
        if (!float.IsFinite(skill.Start)) return "A skill's start level is a number.";
        return null;
    }

    /// <summary>
    /// Adds <paramref name="skill"/> after the others; throws when <see cref="Refusal"/> refuses it.
    /// </summary>
    public static void Add(ModSkill skill)
    {
        if (Refusal(skill, []) is { } refusal) throw new ArgumentException(refusal, nameof(skill));

        skill.Stat = (StatsEnumerated)(FirstStat + Skills.Count);
        Skills.Add(skill);
    }

    /// <summary>
    /// The skill whose <see cref="ModSkill.Stat"/> is <paramref name="stat"/>; null for any other number (every number below
    /// <see cref="FirstStat"/>, and every number without mods).
    /// </summary>
    public static ModSkill? Of(StatsEnumerated stat) =>
        (uint)((int)stat - FirstStat) < (uint)Skills.Count ? Skills[(int)stat - FirstStat] : null;

    /// <summary>
    /// The skill with id <paramref name="id"/> (case-insensitive, as ids are unique); null when no mod added one.
    /// </summary>
    public static ModSkill? Find(string id)
    {
        foreach (var skill in Skills)
            if (string.Equals(skill.Id, id, StringComparison.OrdinalIgnoreCase)) return skill;
        return null;
    }

    /// <summary>
    /// <paramref name="stats"/>' levels in every skill, in registration order.
    /// </summary>
    public static float[] Levels(CharacterStats stats)
    {
        var levels = new float[Skills.Count];
        for (int i = 0; i < levels.Length; i++) levels[i] = Skills[i].Level(stats);
        return levels;
    }

    /// <summary>
    /// Removes every skill (tests).
    /// </summary>
    internal static void Clear() => Skills.Clear();
}
