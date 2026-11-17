using Meitou.Data.Fcs;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Saving;

/// <summary>
/// A player squad's code mod settings on its PLATOON record (docs/formats/save.md "Player platoons"; Meitou only), each only while set: kept out
/// of the faction size count, <c>meitou uncounted</c> (bool: <see cref="Squad.Uncounted"/>) and <c>meitou uncounted owner</c> (string:
/// <see cref="Squad.UncountedOwner"/>); its own purse, <c>meitou purse</c> (string: <see cref="Squad.Money"/> in cats, while
/// <see cref="Squad.OwnPurse"/>) and <c>meitou purse owner</c> (string: <see cref="Squad.PurseOwner"/>).
/// </summary>
public static class ModSquadSave
{
    /// <summary>
    /// The PLATOON keys: the count flag and the mod that set it, the purse's money and the mod that set it.
    /// </summary>
    public const string UncountedKey = "meitou uncounted", OwnerKey = "meitou uncounted owner", PurseKey = "meitou purse",
        PurseOwnerKey = "meitou purse owner";

    /// <summary>
    /// Writes the squad's flag over the platoon's; a squad without it loses both keys.
    /// </summary>
    public static void Write(FcsRecord platoon, Squad squad)
    {
        if (squad.Uncounted) platoon.Bools[UncountedKey] = true;
        else platoon.Bools.Remove(UncountedKey);

        if (squad is { Uncounted: true, UncountedOwner: { } owner }) platoon.Strings[OwnerKey] = owner;
        else platoon.Strings.Remove(OwnerKey);

        if (squad.OwnPurse) platoon.Strings[PurseKey] = squad.Money.ToString(System.Globalization.CultureInfo.InvariantCulture);
        else platoon.Strings.Remove(PurseKey);

        if (squad is { OwnPurse: true, PurseOwner: { } purseOwner }) platoon.Strings[PurseOwnerKey] = purseOwner;
        else platoon.Strings.Remove(PurseOwnerKey);
    }

    /// <summary>
    /// Gives a loaded player squad the flag and owner its platoon record holds.
    /// </summary>
    public static void Read(FcsRecord platoon, Squad squad)
    {
        squad.Uncounted = platoon.Bools.GetValueOrDefault(UncountedKey);
        squad.UncountedOwner = squad.Uncounted && platoon.Strings.GetValueOrDefault(OwnerKey) is { Length: > 0 } owner ? owner : null;

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (!long.TryParse(platoon.Strings.GetValueOrDefault(PurseKey), System.Globalization.NumberStyles.Integer, culture, out long money)) return;

        squad.OwnPurse = true;
        squad.Money = money;
        squad.PurseOwner = platoon.Strings.GetValueOrDefault(PurseOwnerKey) is { Length: > 0 } purseOwner ? purseOwner : null;
    }
}
