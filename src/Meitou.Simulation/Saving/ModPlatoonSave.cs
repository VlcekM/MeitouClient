using System.Globalization;
using Meitou.Data.Fcs;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Saving;

/// <summary>
/// A code mod's marks on a PLATOON record (engine keys, not in the original, which reads keys by name and ignores them; docs/formats/save.md
/// "NPC platoons"): <c>meitou mod spawn</c> (a bool, only when set) and <c>meitou mod key</c> (its <see cref="Platoon.Key"/> in hex, so a stand-in
/// that never loaded is planned again as the same squad) for a platoon <c>PopulationSystem.SpawnPlatoon</c> made, with <c>meitou mod cap</c> (an
/// int, only when set: its <see cref="Platoon.MemberCap"/>), <c>meitou mod tag</c> (a
/// string, only when set) for <see cref="Platoon.ModTag"/>, one <c>meitou mod tag &lt;mod id&gt;</c> per mod for <see cref="Platoon.ModTags"/>
/// (<see cref="ModTagSave"/>), and a hold a mod took in its name: <c>meitou mod hold</c> (the mod id), <c>meitou mod goal</c> (a point, Y 0),
/// <c>meitou mod follow</c> (the followed character's lasting id in hex) and <c>meitou mod arrived</c> (a bool), each only when set.
/// </summary>
public static class ModPlatoonSave
{
    /// <summary>
    /// The PLATOON keys: the mod-spawn flag and the mod tag.
    /// </summary>
    public const string SpawnKey = "meitou mod spawn", KeyKey = "meitou mod key", TagKey = "meitou mod tag";

    /// <summary>
    /// The PLATOON key of a mod spawn's member cap.
    /// </summary>
    public const string CapKey = "meitou mod cap";

    /// <summary>
    /// The PLATOON keys of a hold a mod took in its name (<see cref="Platoon.HoldOwner"/>).
    /// </summary>
    public const string HoldKey = "meitou mod hold", GoalKey = "meitou mod goal", FollowKey = "meitou mod follow", ArrivedKey = "meitou mod arrived";

    /// <summary>
    /// Writes the platoon's marks on its record and removes any it no longer has, so an unmarked platoon's record holds none of the keys.
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

        if (platoon.ModSpawned && platoon.MemberCap > 0) record.Ints[CapKey] = platoon.MemberCap;
        else record.Ints.Remove(CapKey);

        if (platoon.ModTag.Length > 0) record.Strings[TagKey] = platoon.ModTag;
        else record.Strings.Remove(TagKey);

        ModTagSave.Write(record, platoon.ModTags);
        WriteHold(record, platoon);
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
    /// A mod spawn's member cap; 0 when the record has none.
    /// </summary>
    public static int Cap(FcsRecord record) => Math.Max(0, record.Ints.GetValueOrDefault(CapKey));

    /// <summary>
    /// The record's mod tag ("" when it has none).
    /// </summary>
    public static string Tag(FcsRecord record) => record.Strings.GetValueOrDefault(TagKey) ?? "";

    /// <summary>
    /// Puts the record's per-mod tags and named hold on <paramref name="platoon"/>: held in that mod's name with its goal, its followed character's
    /// lasting id (found again once the character is in the world) and its told arrival. A record without a hold leaves the platoon unheld.
    /// </summary>
    public static void Read(FcsRecord record, Platoon platoon)
    {
        ModTagSave.Read(record, platoon.ModTags);
        if (record.Strings.GetValueOrDefault(HoldKey) is not { Length: > 0 } owner) return;

        platoon.Held = true;
        platoon.HoldOwner = owner;
        if (record.Vector3S.TryGetValue(GoalKey, out var goal)) platoon.ModGoal = new(goal.X, goal.Z);
        if (record.Strings.GetValueOrDefault(FollowKey) is { } hex
            && ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong lasting))
            platoon.ModFollowLasting = new LastingId(lasting);
        platoon.ModArrived = record.Bools.GetValueOrDefault(ArrivedKey);
    }

    /// <summary>
    /// Writes a named hold's keys, or removes them when the platoon has none.
    /// </summary>
    static void WriteHold(FcsRecord record, Platoon platoon)
    {
        record.Strings.Remove(HoldKey);
        record.Vector3S.Remove(GoalKey);
        record.Strings.Remove(FollowKey);
        record.Bools.Remove(ArrivedKey);
        if (platoon.HoldOwner is not { } owner) return;

        record.Strings[HoldKey] = owner;
        if (platoon.ModGoal is { } goal) record.Vector3S[GoalKey] = new(goal.X, 0, goal.Y);
        if (!platoon.ModFollowLasting.IsNone) record.Strings[FollowKey] = platoon.ModFollowLasting.ToString();
        if (platoon.ModArrived) record.Bools[ArrivedKey] = true;
    }
}
