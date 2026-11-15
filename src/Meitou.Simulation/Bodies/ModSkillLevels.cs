namespace Meitou.Simulation.Bodies;

/// <summary>
/// One character's levels in code mods' skills by skill id, ordinal order. Holds ids no loaded mod registers too (a save made with a mod that is
/// not loaded now), so they are saved again unchanged.
/// </summary>
public sealed class ModSkillLevels
{
    /// <summary>
    /// The STATS key prefix of a level: <c>meitou skill &lt;id&gt;</c>.
    /// </summary>
    public const string SaveKeyPrefix = "meitou skill ";

    /// <summary>
    /// The levels by skill id.
    /// </summary>
    readonly SortedDictionary<string, float> _levels = new(StringComparer.Ordinal);

    /// <summary>
    /// The number of skills with a level.
    /// </summary>
    public int Count => _levels.Count;

    /// <summary>
    /// The level in skill <paramref name="id"/>; null when it has none.
    /// </summary>
    public float? Get(string id) => _levels.TryGetValue(id, out float level) ? level : null;

    /// <summary>
    /// Sets the level in skill <paramref name="id"/>.
    /// </summary>
    public void Set(string id, float level) => _levels[id] = level;

    /// <summary>
    /// Writes the levels into a STATS record's floats under <see cref="SaveKeyPrefix"/>.
    /// </summary>
    internal void WriteSave(IDictionary<string, float> floats)
    {
        foreach (var (id, level) in _levels) floats[SaveKeyPrefix + id] = level;
    }

    /// <summary>
    /// The levels saved in a STATS record's floats; null when it has none.
    /// </summary>
    internal static ModSkillLevels? ReadSave(IReadOnlyDictionary<string, float> floats)
    {
        ModSkillLevels? levels = null;
        foreach (var (key, value) in floats)
        {
            if (!key.StartsWith(SaveKeyPrefix, StringComparison.Ordinal) || key.Length == SaveKeyPrefix.Length) continue;
            (levels ??= new()).Set(key[SaveKeyPrefix.Length..], value);
        }

        return levels;
    }

    /// <summary>
    /// Adds the levels to the state hash, in id order.
    /// </summary>
    internal void AddTo(ref StateHasher h)
    {
        h.Add(_levels.Count);
        foreach (var (id, level) in _levels)
        {
            h.Add(Rng.StableHash(id));
            h.Add(level);
        }
    }
}
