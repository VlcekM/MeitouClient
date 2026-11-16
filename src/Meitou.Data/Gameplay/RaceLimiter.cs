using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay;

/// <summary>
/// Who may wear an item (the original's <c>RaceLimiter</c>, <c>FUN_140763150</c> / <c>FUN_140593f00</c>, <b>Verified</b>): a non-empty <c>races</c>
/// list names the only races, and then <c>races exclude</c> is not read; otherwise <c>races exclude</c> names the races that may not. With neither,
/// anyone but an animal may. One predicate for the equip checks, the loadout a generated character is dressed with and the item info; the mods'
/// <see cref="WearRules"/> are asked after the lists pass.
/// </summary>
public static class RaceLimiter
{
    /// <summary>
    /// The <c>races</c> and <c>races exclude</c> RACE ids of an item record (the exclude list is empty when <c>races</c> is not).
    /// </summary>
    public static (string[] Only, string[] Never) Of(GameRecord item)
    {
        string[] only = [.. item.GetReferences("races").Select(r => r.TargetStringId)];
        return (only, only.Length > 0 ? [] : [.. item.GetReferences("races exclude").Select(r => r.TargetStringId)]);
    }

    /// <summary>
    /// Whether a wearer of RACE <paramref name="race"/> (null: no wearer, no limit) passes the two lists and, for the item record
    /// <paramref name="item"/> (null: none asked), the mods' <see cref="WearRules"/>.
    /// </summary>
    public static bool Allows(string[] only, string[] never, string? race, bool animal = false, string? item = null) =>
        race is null || (Listed(only, never, race, animal) && (item is null || WearRules.Allows(item, race, animal)));

    /// <summary>
    /// Whether a person (not an animal) of <paramref name="race"/> may wear the item record: its race lists, then the mods' <see cref="WearRules"/>.
    /// </summary>
    public static bool Allows(GameRecord item, GameRecord race)
    {
        var (only, never) = Of(item);
        return Allows(only, never, race.StringId, item: item.StringId);
    }

    /// <summary>
    /// Whether a wearer of <paramref name="race"/> passes the two lists alone.
    /// </summary>
    static bool Listed(string[] only, string[] never, string race, bool animal) =>
        only.Length == 0 ? !animal && !never.Contains(race) : only.Contains(race);
}
