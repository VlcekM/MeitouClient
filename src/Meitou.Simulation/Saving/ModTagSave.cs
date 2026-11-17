using Meitou.Data.Fcs;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The code mods' tags on a player squad's PLATOON record or a player town's GAMESTATE_TOWN (docs/formats/save.md; Meitou only): one string key
/// per mod, <c>meitou mod tag &lt;mod id&gt;</c>, holding that mod's tag (<see cref="Factions.Squad.ModTags"/>,
/// <see cref="Construction.PlayerTown.ModTags"/>). The NPC platoons' single <c>meitou mod tag</c> (<see cref="ModPlatoonSave"/>) has no mod id and
/// is not one of these.
/// </summary>
public static class ModTagSave
{
    /// <summary>
    /// The start of every key; the mod's manifest id follows.
    /// </summary>
    public const string Prefix = "meitou mod tag ";

    /// <summary>
    /// Writes <paramref name="tags"/> on the record, replacing the tags it held; an empty tag is not written.
    /// </summary>
    public static void Write(FcsRecord record, IReadOnlyDictionary<string, string> tags)
    {
        var old = record.Strings.Keys.Where(k => k.StartsWith(Prefix, StringComparison.Ordinal)).ToList();
        foreach (string key in old) record.Strings.Remove(key);

        foreach (var (mod, tag) in tags)
        {
            if (mod.Length > 0 && tag.Length > 0) record.Strings[Prefix + mod] = tag;
        }
    }

    /// <summary>
    /// Adds the record's tags to <paramref name="tags"/>; a mod already there keeps its tag (a town's first state wins).
    /// </summary>
    public static void Read(FcsRecord record, IDictionary<string, string> tags)
    {
        foreach (var (key, tag) in record.Strings)
        {
            if (!key.StartsWith(Prefix, StringComparison.Ordinal) || key.Length == Prefix.Length || tag.Length == 0) continue;

            tags.TryAdd(key[Prefix.Length..], tag);
        }
    }
}
