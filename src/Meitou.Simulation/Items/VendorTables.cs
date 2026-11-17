using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Economy;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Items;

/// <summary>
/// The game session's vendor list tables (the original's list cache <c>FUN_1409592a0</c>, <b>Verified</b>, decompile; docs/game/economy.md
/// "Building shops"): one table per (list, faction), built once and kept until the session ends (the "Reset game" step <c>FUN_14036cb80</c>, run
/// by a load, an import and a new game, deletes them all, <c>FUN_1409543a0</c>). Shop fills, item slots, camp loot and pack animal loads all draw
/// from them; each fetch first puts back what was taken from the table since the last one (<see cref="VendorTable"/>). Kept by the world, not saved.
/// </summary>
public sealed class VendorTables
{
    /// <summary>
    /// The tables made so far, by list record id and faction index.
    /// </summary>
    readonly Dictionary<(string List, int Faction), VendorTable> _tables = [];

    /// <summary>
    /// Each faction's forbidden items, found once.
    /// </summary>
    readonly Dictionary<int, HashSet<string>> _forbidden = [];

    /// <summary>
    /// The table of <paramref name="list"/> for <paramref name="faction"/> (a faction index; none: -1): made on first use, else with what was taken
    /// from it put back first.
    /// </summary>
    public VendorTable Fetch(PopulationData data, VendorList list, int faction)
    {
        if (_tables.TryGetValue((list.Id, faction), out var table))
        {
            table.PutBack();
            return table;
        }

        table = new VendorTable(Build(data, list, faction));
        _tables[(list.Id, faction)] = table;
        return table;
    }

    /// <summary>
    /// The list's entries as the list builder takes them (<c>FUN_140956ba0</c>, <b>Verified</b>): those of weight above 0, but for the faction the
    /// ITEM entries it forbids (<see cref="Forbidden"/>).
    /// </summary>
    List<VendorEntry> Build(PopulationData data, VendorList list, int faction)
    {
        var forbidden = Forbidden(data, faction);
        return [.. list.Entries.Where(e => e.Weight > 0 && (e.Type != FcsRecordType.Item || !forbidden.Contains(e.Id)))];
    }

    /// <summary>
    /// The items the builder's <c>items</c> branch leaves out for <paramref name="faction"/> (<b>Verified</b>, decompile; the other branches test
    /// nothing, and with no faction nothing is left out): the <c>forbidden items</c> of the FACTION record itself and of each of its <c>trade
    /// culture</c> records (<c>FUN_1407fe270</c>, <c>FUN_1407fd9a0</c>). The table is keyed by list and faction, so a town's culture plays no part.
    /// </summary>
    HashSet<string> Forbidden(PopulationData data, int faction)
    {
        if (_forbidden.TryGetValue(faction, out var known)) return known;

        var forbidden = new HashSet<string>(StringComparer.Ordinal);
        if ((uint)faction < (uint)data.Factions.Count)
        {
            var info = data.Factions[faction];
            if (data.Db.Find(info.Id) is { } record)
                foreach (var r in record.GetReferences("forbidden items")) forbidden.Add(r.TargetStringId);
            if (TradeCulture.From(data.Db, info.TradeCulture) is { } culture) forbidden.UnionWith(culture.Forbidden);
        }

        _forbidden[faction] = forbidden;
        return forbidden;
    }
}

/// <summary>
/// One list's table for one faction (<see cref="VendorTables"/>): its weighted entries, and the blueprints and maps taken out of it since the last
/// fetch.
/// </summary>
public sealed class VendorTable(List<VendorEntry> entries)
{
    /// <summary>
    /// The entries a pick draws from, in the table's order.
    /// </summary>
    public List<VendorEntry> Entries { get; } = entries;

    /// <summary>
    /// The records taken out since the last fetch, in the order taken, each with its weight map's weight.
    /// </summary>
    readonly List<VendorEntry> _taken = [];

    /// <summary>
    /// A blueprint or map made anew from <paramref name="entry"/> leaves the table (<c>FUN_1409540d0</c>, <c>FUN_1409555d0</c>: every entry of its
    /// record, <c>FUN_140969100</c>), noted with the weight its weight map holds, that of its last entry.
    /// </summary>
    public void Take(VendorEntry entry)
    {
        _taken.Add(Entries.FindLast(e => e.Id == entry.Id));
        Entries.RemoveAll(e => e.Id == entry.Id);
    }

    /// <summary>
    /// What was taken goes back (<c>FUN_140953250</c>): each record once, at the end of the table, with its noted weight; so a record listed twice
    /// comes back once.
    /// </summary>
    internal void PutBack()
    {
        Entries.AddRange(_taken);
        _taken.Clear();
    }
}
