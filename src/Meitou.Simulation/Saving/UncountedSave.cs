using Meitou.Data.Fcs;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Saving;

/// <summary>
/// A player squad a code mod keeps out of the faction size count, on its PLATOON record (docs/formats/save.md "Player platoons"; Meitou only):
/// <c>meitou uncounted</c> (bool: <see cref="Squad.Uncounted"/>) and <c>meitou uncounted owner</c> (string: <see cref="Squad.UncountedOwner"/>),
/// each only while set.
/// </summary>
public static class UncountedSave
{
    /// <summary>
    /// The PLATOON keys: the flag and the mod that set it.
    /// </summary>
    public const string UncountedKey = "meitou uncounted", OwnerKey = "meitou uncounted owner";

    /// <summary>
    /// Writes the squad's flag over the platoon's; a squad without it loses both keys.
    /// </summary>
    public static void Write(FcsRecord platoon, Squad squad)
    {
        if (squad.Uncounted) platoon.Bools[UncountedKey] = true;
        else platoon.Bools.Remove(UncountedKey);

        if (squad is { Uncounted: true, UncountedOwner: { } owner }) platoon.Strings[OwnerKey] = owner;
        else platoon.Strings.Remove(OwnerKey);
    }

    /// <summary>
    /// Gives a loaded player squad the flag and owner its platoon record holds.
    /// </summary>
    public static void Read(FcsRecord platoon, Squad squad)
    {
        squad.Uncounted = platoon.Bools.GetValueOrDefault(UncountedKey);
        squad.UncountedOwner = squad.Uncounted && platoon.Strings.GetValueOrDefault(OwnerKey) is { Length: > 0 } owner ? owner : null;
    }
}
