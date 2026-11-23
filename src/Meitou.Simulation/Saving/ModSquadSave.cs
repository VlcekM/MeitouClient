using Meitou.Data.Fcs;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Saving;

/// <summary>
/// A player squad's code mod settings on its PLATOON record (docs/formats/save.md "Player platoons"; Meitou only), each only while set: kept out
/// of the faction size count, <c>meitou uncounted</c> (bool: <see cref="Squad.Uncounted"/>) and <c>meitou uncounted owner</c> (string:
/// <see cref="Squad.UncountedOwner"/>); its own purse, <c>meitou purse</c> (string: <see cref="Squad.Money"/> in cats, while
/// <see cref="Squad.OwnPurse"/>) and <c>meitou purse owner</c> (string: <see cref="Squad.PurseOwner"/>); asleep, <c>meitou dormant</c> (bool:
/// <see cref="Squad.Dormant"/>) and <c>meitou dormant owner</c> (string: <see cref="Squad.DormantOwner"/>); its own squad AI options,
/// <c>meitou squad ai</c> (int: <see cref="Squad.OwnOptions"/>'s flags) and <c>meitou squad ai owner</c> (string: <see cref="Squad.OptionsOwner"/>).
/// </summary>
public static class ModSquadSave
{
    /// <summary>
    /// The PLATOON keys: the count flag, the purse's money, the sleep flag and the squad AI options, each with the mod that set it.
    /// </summary>
    public const string UncountedKey = "meitou uncounted", OwnerKey = "meitou uncounted owner", PurseKey = "meitou purse",
        PurseOwnerKey = "meitou purse owner", DormantKey = "meitou dormant", DormantOwnerKey = "meitou dormant owner",
        OptionsKey = "meitou squad ai", OptionsOwnerKey = "meitou squad ai owner";

    /// <summary>
    /// Writes the squad's mod settings over the platoon's; a setting the squad no longer has loses its keys.
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

        if (squad.Dormant) platoon.Bools[DormantKey] = true;
        else platoon.Bools.Remove(DormantKey);

        if (squad is { Dormant: true, DormantOwner: { } dormantOwner }) platoon.Strings[DormantOwnerKey] = dormantOwner;
        else platoon.Strings.Remove(DormantOwnerKey);

        if (squad.OwnOptions is { } options) platoon.Ints[OptionsKey] = (int)options;
        else platoon.Ints.Remove(OptionsKey);

        if (squad is { OwnOptions: not null, OptionsOwner: { } optionsOwner }) platoon.Strings[OptionsOwnerKey] = optionsOwner;
        else platoon.Strings.Remove(OptionsOwnerKey);
    }

    /// <summary>
    /// Gives a loaded player squad the mod settings and owners its platoon record holds.
    /// </summary>
    public static void Read(FcsRecord platoon, Squad squad)
    {
        squad.Uncounted = platoon.Bools.GetValueOrDefault(UncountedKey);
        squad.UncountedOwner = squad.Uncounted && platoon.Strings.GetValueOrDefault(OwnerKey) is { Length: > 0 } owner ? owner : null;
        squad.Dormant = platoon.Bools.GetValueOrDefault(DormantKey);
        squad.DormantOwner = squad.Dormant && platoon.Strings.GetValueOrDefault(DormantOwnerKey) is { Length: > 0 } sleeper ? sleeper : null;
        squad.OwnOptions = platoon.Ints.TryGetValue(OptionsKey, out int options) ? (Player.SquadAi)options : null;
        squad.OptionsOwner = squad.OwnOptions is not null && platoon.Strings.GetValueOrDefault(OptionsOwnerKey) is { Length: > 0 } chooser
            ? chooser : null;

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (!long.TryParse(platoon.Strings.GetValueOrDefault(PurseKey), System.Globalization.NumberStyles.Integer, culture, out long money)) return;

        squad.OwnPurse = true;
        squad.Money = money;
        squad.PurseOwner = platoon.Strings.GetValueOrDefault(PurseOwnerKey) is { Length: > 0 } purseOwner ? purseOwner : null;
    }
}
