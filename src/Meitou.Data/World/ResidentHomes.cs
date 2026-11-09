using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Data.World;

/// <summary>
/// Which building of a town each resident entry's squads get (docs/game/factions-squads-towns.md "Town residents"), and the buildings that assignment ruins
/// (docs/game/buildings-production.md "Ruined by residents"). Pure and seed-free, so the level data's ruins, laid out at boot, and the population's homes, made when a
/// town loads, agree.
/// </summary>
public static class ResidentHomes
{
    sealed class Entry
    {
        public required string Id;
        public required SquadTemplate Template;
        public int Count;
        public int Priority;
        public required HashSet<string> Preferred, Disliked;
    }

    /// <summary>
    /// The resident table of a town (Observed): without <c>residents override</c> the faction's <c>residents</c> first, then the town's; with it the town's only. A
    /// template listed again is dropped (the first entry wins); then v0 = 0 entries are dropped. A town with no faction has none (<c>FUN_140936e60</c> returns first).
    /// </summary>
    public static List<RecordLink> Entries(TownData town, FactionData? faction)
    {
        var list = new List<RecordLink>();
        if (faction is null) return list;
        if (!town.ResidentsOverride) list.AddRange(faction.Residents);
        list.AddRange(town.Residents);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        list.RemoveAll(e => !seen.Add(e.Id));
        list.RemoveAll(e => e.V0 <= 0);
        return list;
    }

    /// <summary>
    /// The town's <c>default resident</c> (first entry), else (without <c>residents override</c>) the faction's; null when neither names one or the
    /// town has no faction.
    /// </summary>
    public static RecordLink? DefaultResident(TownData town, FactionData? faction) =>
        faction is null ? null
        : town.DefaultResident.Count > 0 ? town.DefaultResident[0]
        : !town.ResidentsOverride && faction.DefaultResident.Count > 0 ? faction.DefaultResident[0] : null;

    /// <summary>
    /// A SQUAD_TEMPLATE or UNIQUE_SQUAD_TEMPLATE record.
    /// </summary>
    public static bool IsTemplate(GameRecord? record) => record is { Type: FcsRecordType.SquadTemplate or FcsRecordType.UniqueSquadTemplate };

    /// <summary>
    /// Houses the town's resident entries in <paramref name="buildings"/> (those that can house, are not <paramref name="playerFactionId"/>'s in the level data and not among
    /// <paramref name="playerOwned"/>, the keys of those the player owns now, such as a house bought); returns each housed
    /// building with its template, in housing order. Order of the buildings: by a hash of their keys (<b>engine choice</b>: the original's is its handles' serials, which
    /// are random, so it rerolls homes at a new game; a fixed order lets the boot lay out the ruins the homes make). Steps:
    /// <list type="number">
    /// <item>priorities 5 down to 0: first (<b>engine choice</b>) a building whose level-data state names a template of this priority keeps it while the entry has
    /// squads left (the level data's assignment is one the original's rules made); then the original's rule (Observed,
    /// <c>FUN_1409370d0</c>, <c>FUN_14092e870</c>): each entry of the priority one building per pass, passes repeated while one placed a squad and has more; the building
    /// is the free one of the highest weight, the first of them on a tie; an entry with no building left makes no more squads;</item>
    /// <item>every free building left gets the <c>default resident</c> when it takes the template's <c>layout interior</c>.</item>
    /// </list>
    /// </summary>
    public static List<(TownBuilding Building, SquadTemplate Template)> Assign(GameDatabase db, TownData town, FactionData? faction, IReadOnlyList<TownBuilding> buildings,
        string? playerFactionId, IReadOnlySet<string>? playerOwned = null)
    {
        var order = Order(buildings, playerFactionId, playerOwned);
        var entries = MakeEntries(db, Entries(town, faction));
        var housed = new List<(TownBuilding Building, SquadTemplate Template)>();
        var taken = new HashSet<TownBuilding>(ReferenceEqualityComparer.Instance);
        for (int priority = 5; priority >= 0; priority--)
        {
            KeepLevelHomes(entries, priority, order, taken, housed);
            HousePriority(entries, priority, order, taken, housed);
        }
        HouseDefault(db, town, faction, order, taken, housed);
        return housed;
    }

    static List<TownBuilding> Order(IReadOnlyList<TownBuilding> buildings, string? playerFactionId, IReadOnlySet<string>? playerOwned)
    {
        bool Candidate(TownBuilding b) => b.CanHouse && (playerFactionId is null || b.OwnerFactionId != playerFactionId) && playerOwned?.Contains(b.Key) != true;
        return [.. buildings.Where(Candidate).OrderBy(b => KeyHash(b.Key)).ThenBy(b => b.Key, StringComparer.Ordinal)];
    }

    /// <summary>
    /// FNV-1a over the key's characters: stable across runs and platforms.
    /// </summary>
    static ulong KeyHash(string key)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in key)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        return h;
    }

    static List<Entry> MakeEntries(GameDatabase db, List<RecordLink> table)
    {
        var entries = new List<Entry>();
        foreach (var link in table)
        {
            if (db.Find(link.Id) is not { } record || !IsTemplate(record)) continue;
            var t = SquadTemplate.From(record);
            entries.Add(new Entry
            {
                Id = link.Id,
                Template = t,
                Count = link.V0,
                Priority = link.V1 == 100 ? 0 : link.V1,
                Preferred = [.. t.Buildings.Select(b => b.Id)],
                Disliked = [.. t.BuildingDislike.Select(b => b.Id)],
            });
        }
        return entries;
    }

    // The level data's homes of this priority's entries first (engine choice, see Assign).
    static void KeepLevelHomes(List<Entry> entries, int priority, List<TownBuilding> order, HashSet<TownBuilding> taken, List<(TownBuilding, SquadTemplate)> housed)
    {
        foreach (var b in order)
        {
            if (b.ResidentsTemplate.Length == 0 || taken.Contains(b)) continue;
            if (entries.FirstOrDefault(e => e.Id == b.ResidentsTemplate && e.Count > 0 && e.Priority == priority) is not { } e || Weight(e, b) <= 0) continue;
            e.Count--;
            taken.Add(b);
            housed.Add((b, e.Template));
        }
    }

    static void HousePriority(List<Entry> entries, int priority, List<TownBuilding> order, HashSet<TownBuilding> taken, List<(TownBuilding, SquadTemplate)> housed)
    {
        bool again;
        do
        {
            again = false;
            foreach (var e in entries)
            {
                if (e.Count <= 0 || e.Priority != priority || Choose(e, order, taken) is not { } b) continue;
                e.Count--;
                taken.Add(b);
                housed.Add((b, e.Template));
                if (e.Count > 0) again = true;
            }
        } while (again);
    }

    static void HouseDefault(GameDatabase db, TownData town, FactionData? faction, List<TownBuilding> order, HashSet<TownBuilding> taken, List<(TownBuilding, SquadTemplate)> housed)
    {
        if (DefaultResident(town, faction) is not { } link || db.Find(link.Id) is not { } record || !IsTemplate(record)) return;
        var d = SquadTemplate.From(record);
        foreach (var b in order)
        {
            if (taken.Contains(b) || d.LayoutInterior.Length > 0 && !b.InteriorLayouts.Contains(d.LayoutInterior)) continue;
            taken.Add(b);
            housed.Add((b, d));
        }
    }

    /// <summary>
    /// The weight of building <paramref name="b"/> for entry <paramref name="e"/> (0: not a candidate; Observed, <c>FUN_14092e870</c>): a template with a
    /// <c>layout interior</c> needs a building that takes it (gates excepted); a gate houses only an entry that names buildings; an entry naming buildings without a
    /// layout interior takes only those. Weight 1, x 0.4 when the template's <c>layout exterior</c> does not fit, x 10 for a named building, x 0.05 for a disliked one.
    /// </summary>
    static float Weight(Entry e, TownBuilding b)
    {
        var t = e.Template;
        if (!b.IsGate)
        {
            if (t.LayoutInterior.Length > 0 && !b.InteriorLayouts.Contains(t.LayoutInterior)) return 0;
        }
        else if (e.Preferred.Count == 0) return 0;
        bool preferred = e.Preferred.Contains(b.BuildingId);
        if (t.LayoutInterior.Length == 0 && e.Preferred.Count > 0 && !preferred) return 0;
        float w = t.LayoutExterior.Length > 0 && !b.ExteriorLayouts.Contains(t.LayoutExterior) ? 0.4f : 1;
        if (preferred) w *= 10;
        if (e.Disliked.Contains(b.BuildingId)) w *= 0.05f;
        return w;
    }

    /// <summary>
    /// The free building of the highest weight for <paramref name="e"/>, the first in order on a tie. Nothing here reads the state's layout names, which housing
    /// rewrites (<see cref="ResidentRuins.Apply"/>), so the assignment is the same before and after it.
    /// </summary>
    static TownBuilding? Choose(Entry e, List<TownBuilding> order, HashSet<TownBuilding> taken)
    {
        TownBuilding? best = null;
        float bestWeight = 0;
        foreach (var b in order)
        {
            if (taken.Contains(b)) continue;
            float w = Weight(e, b);
            if (w > bestWeight) (best, bestWeight) = (b, w);
        }
        return best;
    }
}