using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Data.World;

/// <summary>
/// Which building of a town each resident entry's squads get (docs/game/factions-squads-towns.md "Town residents"), and the buildings that assignment
/// ruins (docs/game/buildings-production.md "Ruined by residents"). Pure and seed-free, as the original's is for level buildings, so the level
/// data's ruins, laid out at boot, and the population's homes, made when a town loads, agree.
/// </summary>
public static class ResidentHomes
{
    /// <summary>
    /// One resident entry while the town is housed: its template, the squads it has left to house and its priority.
    /// </summary>
    sealed class Entry
    {
        /// <summary>
        /// The template's string id.
        /// </summary>
        public required string Id;
        /// <summary>
        /// The squad template.
        /// </summary>
        public required SquadTemplate Template;
        /// <summary>
        /// Squads left to house (the entry's v0 to start with).
        /// </summary>
        public int Count;
        /// <summary>
        /// The entry's v1, 100 read as 0.
        /// </summary>
        public int Priority;
        /// <summary>
        /// The template's named buildings (<c>buildings</c>) and its disliked ones (<c>building dislike</c>), by string id.
        /// </summary>
        public required HashSet<string> Preferred, Disliked;
    }

    /// <summary>
    /// The resident table of a town (Observed): without <c>residents override</c> the faction's <c>residents</c> first, then the town's; with it the
    /// town's only. A template listed again is dropped (the first entry wins); then v0 = 0 entries are dropped. A town with no faction has none
    /// (<c>FUN_140936e60</c> returns first).
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
    /// The homes made so far while a town is housed: each housed building's template, and which buildings entries may still take.
    /// </summary>
    sealed class Homes(string? defaultId)
    {
        /// <summary>
        /// Each housed building's template.
        /// </summary>
        readonly Dictionary<TownBuilding, SquadTemplate> _templates = new(ReferenceEqualityComparer.Instance);
        /// <summary>
        /// The housed buildings, in the order they were last housed.
        /// </summary>
        readonly List<TownBuilding> _order = [];

        /// <summary>
        /// Whether nobody was housed in <paramref name="b"/>.
        /// </summary>
        public bool Empty(TownBuilding b) => !_templates.ContainsKey(b);

        /// <summary>
        /// Whether an entry may take <paramref name="b"/> (<c>FUN_14092e870</c>, Verified): it holds no template, or the town's default resident's,
        /// so a home of an entry whose template is also the default resident is taken again by a later entry.
        /// </summary>
        public bool Free(TownBuilding b) => !_templates.TryGetValue(b, out var t) || t.Id == defaultId;

        /// <summary>
        /// Houses <paramref name="t"/> in <paramref name="b"/>, replacing who was there.
        /// </summary>
        public void House(TownBuilding b, SquadTemplate t)
        {
            if (_templates.ContainsKey(b)) _order.Remove(b);

            _templates[b] = t;
            _order.Add(b);
        }

        /// <summary>
        /// Each housed building with its template, in housing order.
        /// </summary>
        public List<(TownBuilding Building, SquadTemplate Template)> All() => [.. _order.Select(b => (b, _templates[b]))];
    }

    /// <summary>
    /// Houses the town's resident entries in <paramref name="buildings"/> (those that can house, are not <paramref name="playerFactionId"/>'s in the
    /// level data and not among <paramref name="playerOwned"/>, the keys of those the player owns now, such as a house bought); returns each housed
    /// building with its template, in housing order. Order of the buildings: by their handles' serials (<see cref="Order"/>), as the original's.
    /// The level state's <c>residents template</c> is not read: the original's state reader drops a non-empty one (<c>FUN_140552b70</c>, Verified),
    /// so a new game houses every town afresh. Steps:
    /// <list type="number">
    /// <item>priorities 5 down to 0 (Verified, <c>FUN_1409370d0</c>, <c>FUN_14092e870</c>): each entry of the priority one building per pass,
    /// passes repeated while one placed a squad and has more; the building is the free one (<see cref="Homes.Free"/>) of the highest weight, the
    /// first of them on a tie; an entry with no building left makes no more squads;</item>
    /// <item>every building left with nobody gets the <c>default resident</c> when it takes the template's <c>layout interior</c>.</item>
    /// </list>
    /// </summary>
    public static List<(TownBuilding Building, SquadTemplate Template)> Assign(GameDatabase db, TownData town, FactionData? faction,
        IReadOnlyList<TownBuilding> buildings, string? playerFactionId, IReadOnlySet<string>? playerOwned = null)
    {
        var order = Order(buildings, playerFactionId, playerOwned);
        var entries = MakeEntries(db, Entries(town, faction));
        var fallback = DefaultTemplate(db, town, faction);
        var homes = new Homes(fallback?.Id);
        for (int priority = 5; priority >= 0; priority--) HousePriority(entries, priority, order, homes);

        if (fallback is not null) HouseDefault(fallback, order, homes);

        return homes.All();
    }

    /// <summary>
    /// The town's default resident's template; null when it has none or the record is no squad template.
    /// </summary>
    static SquadTemplate? DefaultTemplate(GameDatabase db, TownData town, FactionData? faction) =>
        DefaultResident(town, faction) is { } link && db.Find(link.Id) is { } record && IsTemplate(record) ? SquadTemplate.From(record) : null;

    /// <summary>
    /// The buildings that may house residents, in housing order: those that can house, not the player's in the level data and not among
    /// <paramref name="playerOwned"/>, by their handle's serial as a signed int, lowest first (the original sorts the zone's buildings so before it
    /// houses a town, <c>FUN_140a0e760</c> with the comparator at <c>0x140a089d0</c>, Verified). A level building's serial is its level state's
    /// <c>handleS</c>, which the original keeps (Verified against saves, docs/game/factions-squads-towns.md "Town residents"); ties, and
    /// buildings with no state, by key (engine choice).
    /// </summary>
    static List<TownBuilding> Order(IReadOnlyList<TownBuilding> buildings, string? playerFactionId, IReadOnlySet<string>? playerOwned)
    {
        bool Candidate(TownBuilding b) =>
            b.CanHouse && (playerFactionId is null || b.OwnerFactionId != playerFactionId) && playerOwned?.Contains(b.Key) != true;

        return [.. buildings.Where(Candidate).OrderBy(b => b.HandleSerial).ThenBy(b => b.Key, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The entries of <paramref name="table"/> whose record is a squad template.
    /// </summary>
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

    /// <summary>
    /// The original's rule for one priority: each entry of the priority takes one building per pass (<see cref="Choose"/>), the passes repeated while
    /// one placed a squad and has more. A building that refuses the entry (<see cref="Refuses"/>) stays as it was and the entry keeps its count, so
    /// it is chosen again and the entry houses nobody more (the original's driver counts a squad only when the housing <c>FUN_14092e4b0</c>
    /// succeeds, Verified).
    /// </summary>
    static void HousePriority(List<Entry> entries, int priority, List<TownBuilding> order, Homes homes)
    {
        bool again;
        do
        {
            again = false;
            foreach (var e in entries)
            {
                if (e.Count <= 0 || e.Priority != priority || Choose(e, order, homes) is not { } b || Refuses(b, e.Template)) continue;

                e.Count--;
                homes.House(b, e.Template);
                if (e.Count > 0) again = true;
            }
        } while (again);
    }

    /// <summary>
    /// Whether housing <paramref name="t"/> in <paramref name="b"/> fails (<c>FUN_14092e4b0</c>, Verified): the template names a <c>layout
    /// interior</c> the building does not offer. <see cref="Weight"/> already leaves such buildings out, except gates.
    /// </summary>
    static bool Refuses(TownBuilding b, SquadTemplate t) => t.LayoutInterior.Length > 0 && !b.InteriorLayouts.Contains(t.LayoutInterior);

    /// <summary>
    /// Gives the default resident <paramref name="d"/> every building with nobody that takes its <c>layout interior</c>.
    /// </summary>
    static void HouseDefault(SquadTemplate d, List<TownBuilding> order, Homes homes)
    {
        foreach (var b in order)
        {
            if (homes.Empty(b) && !Refuses(b, d)) homes.House(b, d);
        }
    }

    /// <summary>
    /// The weight of building <paramref name="b"/> for entry <paramref name="e"/> (0: not a candidate; Verified, <c>FUN_14092e870</c>): a template
    /// with a <c>layout interior</c> needs a building that takes it (gates excepted); a gate houses only an entry that names buildings; an entry
    /// naming buildings without a layout interior takes only those. Weight 1, x 0.4 when the template's <c>layout exterior</c> does not fit, x 10 for
    /// a named building, x 0.05 for a disliked one.
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
    /// The free building of the highest weight for <paramref name="e"/>, the first in order on a tie. Nothing here reads the state's layout names,
    /// which housing rewrites (<see cref="ResidentRuins.Apply"/>), so the assignment is the same before and after it.
    /// </summary>
    static TownBuilding? Choose(Entry e, List<TownBuilding> order, Homes homes)
    {
        TownBuilding? best = null;
        float bestWeight = 0;
        foreach (var b in order)
        {
            if (!homes.Free(b)) continue;

            float w = Weight(e, b);
            if (w > bestWeight) (best, bestWeight) = (b, w);
        }
        return best;
    }
}