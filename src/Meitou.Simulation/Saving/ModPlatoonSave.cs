using System.Globalization;
using Meitou.Data.Fcs;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Saving;

/// <summary>
/// A code mod's marks on a PLATOON record (engine keys, not in the original, which reads keys by name and ignores them; docs/formats/save.md
/// "NPC platoons"): <c>meitou mod spawn</c> (a bool, only when set) and <c>meitou mod key</c> (its <see cref="Platoon.Key"/> in hex, so a stand-in
/// that never loaded is planned again as the same squad) for a platoon <c>PopulationSystem.SpawnPlatoon</c> made, and <c>meitou mod tag</c> (a
/// string, only when set) for <see cref="Platoon.ModTag"/>.
/// </summary>
public static class ModPlatoonSave
{
    /// <summary>
    /// The PLATOON keys: the mod-spawn flag and the mod tag.
    /// </summary>
    public const string SpawnKey = "meitou mod spawn", KeyKey = "meitou mod key", TagKey = "meitou mod tag";

    /// <summary>
    /// Writes the platoon's marks on its record and removes any it no longer has, so an unmarked platoon's record holds neither key.
    /// </summary>
    public static void Write(FcsRecord record, Platoon platoon)
    {
        if (platoon.ModSpawned)
        {
            record.Bools[SpawnKey] = true;
            record.Strings[KeyKey] = platoon.Key.ToString("x16", CultureInfo.InvariantCulture);
        }
        else
        {
            record.Bools.Remove(SpawnKey);
            record.Strings.Remove(KeyKey);
        }

        if (platoon.ModTag.Length > 0) record.Strings[TagKey] = platoon.ModTag;
        else record.Strings.Remove(TagKey);
    }

    /// <summary>
    /// Whether the record is a code mod's spawn.
    /// </summary>
    public static bool Spawned(FcsRecord record) => record.Bools.GetValueOrDefault(SpawnKey);

    /// <summary>
    /// A mod spawn's saved member key; null when the record has none.
    /// </summary>
    public static ulong? Key(FcsRecord record) =>
        record.Strings.GetValueOrDefault(KeyKey) is { } hex && ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong key)
            ? key
            : null;

    /// <summary>
    /// The record's mod tag ("" when it has none).
    /// </summary>
    public static string Tag(FcsRecord record) => record.Strings.GetValueOrDefault(TagKey) ?? "";
}
