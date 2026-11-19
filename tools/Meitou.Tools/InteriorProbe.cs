using System.Numerics;
using System.Text.RegularExpressions;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Saving;

/// <summary>
/// The <c>interiors</c> mode of <c>meitou-tools probe</c>: every town building's saved interior (the building-list entries whose
/// <c>isInsideBuilding</c> names it, docs/formats/save.md "Zone files") against what Meitou would write for it (<see cref="InteriorSave"/>): the
/// objects and item slots its layouts place, by record and position, and each matched entry's state, key by key. On a save the original wrote,
/// the entries are the original's interior.
/// </summary>
static partial class InteriorProbe
{
    /// <summary>
    /// A saved entry: its instance and its state record.
    /// </summary>
    readonly record struct Entry(FcsInstance Instance, FcsRecord State);

    /// <summary>
    /// What the houses compared so far add up to.
    /// </summary>
    sealed class Tally
    {
        /// <summary>
        /// The houses with saved entries, and those whose furniture entries and layout objects pair off with none left over.
        /// </summary>
        public int Houses, Same;

        /// <summary>
        /// The furniture paired, the entries no layout object matched, the layout objects no entry matched, and the node markers left out.
        /// </summary>
        public int Matched, SavedOnly, LayoutOnly, Nodes;

        /// <summary>
        /// The saved item groups, the layouts' item slots, and how many paired.
        /// </summary>
        public int Groups, Slots, SlotMatches;

        /// <summary>
        /// The paired groups whose saved height is within half a unit of their slot's height above the ground, and the largest gap of all.
        /// </summary>
        public int GroupsLevel;

        /// <summary>
        /// See <see cref="GroupsLevel"/>.
        /// </summary>
        public float GroupHeightGap;

        /// <summary>
        /// The key-by-key differences of the paired states.
        /// </summary>
        public readonly StateDiff Diff = new();
    }

    /// <summary>
    /// Prints each house whose entries and layout objects differ (all of one house when <paramref name="only"/> names its key), then the counts
    /// and how the states Meitou would write differ from the saved ones.
    /// </summary>
    public static void Run(ProbeWorld probe, string? only)
    {
        var inside = EntriesByHouse(probe.Save);
        var states = HeldSave.BuildingStates(probe.Save);
        var tally = new Tally();
        foreach (var b in probe.Data.Sites.SelectMany(s => s.Buildings))
            if (inside.TryGetValue(b.Key, out var entries) && states.TryGetValue(b.Key, out var house)) Compare(probe, b, house, entries, tally, only);

        Console.WriteLine($"houses with entries {tally.Houses} ({tally.Same} agree), matched {tally.Matched}, saved only {tally.SavedOnly}, "
            + $"layout only {tally.LayoutOnly}, nodes left out {tally.Nodes}; item groups saved {tally.Groups}, slots {tally.Slots}, "
            + $"matched {tally.SlotMatches}, {tally.GroupsLevel} within 0.5 of their height above the ground (largest gap {tally.GroupHeightGap:0.##})");
        tally.Diff.Print();
    }

    /// <summary>
    /// Compares one house's saved entries with its layouts' objects and item slots, and prints it when they differ or it is the one asked for.
    /// </summary>
    static void Compare(ProbeWorld probe, TownBuilding b, FcsRecord house, List<Entry> entries, Tally tally, string? only)
    {
        var inHouse = InteriorHouse.Of(house, b);
        var furniture = entries.Where(e => e.State.RecordType == FcsRecordType.GamestateBuilding).ToList();
        var placed = b.LayoutBuildings.Where(p => probe.Db.Find(p.RecordId)?.GetBool("is node") != true).ToList();
        var (pairs, unplaced, unsaved) = Match(furniture, placed, p => (p.RecordId, p.Position));
        tally.Houses++;
        tally.Nodes += b.LayoutBuildings.Count - placed.Count;
        tally.Matched += pairs.Count;
        tally.SavedOnly += unplaced.Count;
        tally.LayoutOnly += unsaved.Count;
        if (unplaced.Count == 0 && unsaved.Count == 0) tally.Same++;
        foreach (var (e, p) in pairs) tally.Diff.Add(e.State, InteriorSave.EntryState(probe.Db, probe.Db.Find(p.RecordId)!, p, inHouse), p.RecordId);

        var slotPairs = CompareGroups(probe, b, entries, inHouse, tally);
        bool detail = only == b.Key;
        if (only is not null ? !detail : unplaced.Count == 0 && unsaved.Count == 0) return;

        Console.WriteLine($"{b.Key} '{b.Name}' entries {furniture.Count}, layout {placed.Count}, matched {pairs.Count}");
        foreach (var e in unplaced) Console.WriteLine($"  saved only   {e.Instance.Target} {Name(probe, e.Instance.Target)} at {e.Instance.Position}");
        foreach (var p in unsaved) Console.WriteLine($"  layout only  {p.RecordId} {Name(probe, p.RecordId)} at {p.Position} {p.Id}");
        if (!detail) return;

        foreach (var (e, p) in pairs) Console.WriteLine(PairLine(probe, e.Instance, p));
        foreach (var (e, s) in slotPairs) Console.WriteLine($"  {s.Id} {s.GroupId} entry={e.Instance.Id} height gap {HeightGap(probe, e, s):0.##}");
    }

    /// <summary>
    /// Pairs a house's saved item groups with its layout's item slots (on the ground plane: a group's entry keeps its height above the terrain)
    /// and adds them to the tally; returns the pairs.
    /// </summary>
    static List<(Entry, TownItemSlot)> CompareGroups(ProbeWorld probe, TownBuilding b, List<Entry> entries, InteriorHouse inHouse, Tally tally)
    {
        var saved = entries.Where(e => e.State.RecordType == FcsRecordType.ItemPlacementGroup).ToList();
        var (pairs, _, _) = Match(saved, b.ItemSlots, s => (s.GroupId, s.Position), flat: true);
        tally.Groups += saved.Count;
        tally.Slots += b.ItemSlots.Count;
        tally.SlotMatches += pairs.Count;
        foreach (var (e, s) in pairs)
        {
            tally.Diff.Add(e.State, InteriorSave.GroupState(s, inHouse), "group");
            float gap = MathF.Abs(HeightGap(probe, e, s));
            if (gap <= 0.5f) tally.GroupsLevel++;
            tally.GroupHeightGap = MathF.Max(tally.GroupHeightGap, gap);
        }

        return pairs;
    }

    /// <summary>
    /// A saved group's height less the height Meitou would write for its slot (<see cref="InteriorSave.GroupPosition"/>).
    /// </summary>
    static float HeightGap(ProbeWorld probe, Entry e, TownItemSlot s) =>
        e.Instance.Position.Y - InteriorSave.GroupPosition(probe.World, s).Y;

    /// <summary>
    /// A record's name; "" when it is not loaded.
    /// </summary>
    static string Name(ProbeWorld probe, string recordId) => probe.Db.Find(recordId)?.Name ?? "";

    /// <summary>
    /// One matched pair: how far apart the entry and the object stand, whether their rotations agree, the floor and the side.
    /// </summary>
    static string PairLine(ProbeWorld probe, FcsInstance e, TownLayoutBuilding p)
    {
        float turn = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(e.Rotation), Quaternion.Normalize(p.Rotation)));
        return $"  {p.Id} {p.RecordId} {Name(probe, p.RecordId)} d={Vector3.Distance(e.Position, p.Position):0.###} turn={turn:0.####} "
            + $"floor={p.Floor} outdoors={p.Outdoors} entry={e.Id}";
    }

    /// <summary>
    /// Pairs each entry with the nearest free object of its record within <see cref="SavedPieces.Reach"/> (<paramref name="flat"/>: on the
    /// ground plane only); the rest of both sides.
    /// </summary>
    static (List<(Entry, T)> Pairs, List<Entry> Unplaced, List<T> Unsaved) Match<T>(
        List<Entry> entries, IReadOnlyList<T> objects, Func<T, (string Record, Vector3 At)> of, bool flat = false)
    {
        float Gap(Vector3 a, Vector3 b) => flat ? Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z)) : Vector3.Distance(a, b);

        var free = objects.ToList();
        var pairs = new List<(Entry, T)>();
        var unplaced = new List<Entry>();
        foreach (var e in entries)
        {
            int best = -1;
            float gap = SavedPieces.Reach;
            for (int i = 0; i < free.Count; i++)
            {
                var (record, at) = of(free[i]);
                if (record != e.Instance.Target || Gap(at, e.Instance.Position) is var d && d > gap) continue;

                (best, gap) = (i, d);
            }

            if (best < 0)
            {
                unplaced.Add(e);
                continue;
            }

            pairs.Add((e, free[best]));
            free.RemoveAt(best);
        }

        return (pairs, unplaced, free);
    }

    /// <summary>
    /// The save's entries inside each building, by the building's key.
    /// </summary>
    static Dictionary<string, List<Entry>> EntriesByHouse(SaveGame save)
    {
        var map = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        foreach (var zone in save.Zones.Values)
        {
            if (zone.BuildingList is not { } list) continue;

            var records = zone.File.Records
                .Where(r => r.RecordType is FcsRecordType.GamestateBuilding or FcsRecordType.ItemPlacementGroup)
                .ToDictionary(r => r.StringId, StringComparer.Ordinal);
            var stated = list.Instances
                .Select(i => (Instance: i, State: i.States.Select(records.GetValueOrDefault).FirstOrDefault(s => s is not null)))
                .Where(e => e.State is not null)
                .Select(e => new Entry(e.Instance, e.State!))
                .ToList();
            var keyOf = new Dictionary<Hand, string>();
            foreach (var e in stated)
                if (Hand.IsComplete(e.State, "handle")) keyOf[Hand.Read(e.State, "handle")] = BuildingKey.Of(zone.Coordinate, e.Instance.Id);

            foreach (var e in stated)
            {
                var parent = Hand.Read(e.State, "isInsideBuilding");
                if (parent.IsNull || !keyOf.TryGetValue(parent, out var house)) continue;

                if (!map.TryGetValue(house, out var entries)) map[house] = entries = [];
                entries.Add(e);
            }
        }

        return map;
    }

    /// <summary>
    /// The differences between saved entry states and the ones Meitou would write, counted by key: keys only the save has, keys only Meitou
    /// writes, and values that differ. Handles, use nodes and a machine's production keys are a later writer's or the session's.
    /// </summary>
    internal sealed partial class StateDiff
    {
        /// <summary>
        /// The count and the records of each difference, by its label.
        /// </summary>
        readonly SortedDictionary<string, (int Count, Dictionary<string, int> Records)> _found = new(StringComparer.Ordinal);

        /// <summary>
        /// How many states were compared.
        /// </summary>
        int _compared;

        /// <summary>
        /// Compares one saved state with the written one of an object of <paramref name="record"/>.
        /// </summary>
        public void Add(FcsRecord saved, FcsRecord written, string record)
        {
            _compared++;
            var a = Values(saved);
            var b = Values(written);
            foreach (var (key, value) in a)
            {
                if (!b.TryGetValue(key, out var mine)) Note("saved only " + key, record);
                else if (!Same(value, mine)) Note($"differs {key}", record);
            }

            foreach (string key in b.Keys)
                if (!a.ContainsKey(key)) Note("written only " + key, record);
        }

        /// <summary>
        /// Prints the differences, the most frequent records of each.
        /// </summary>
        public void Print()
        {
            Console.WriteLine($"states compared {_compared}");
            foreach (var (label, (count, records)) in _found)
            {
                var top = records.OrderByDescending(p => p.Value).Take(5).Select(p => $"{p.Key} x{p.Value}");
                Console.WriteLine($"  {label}: {count} in {records.Count} records ({string.Join(", ", top)})");
            }
        }

        /// <summary>
        /// Counts one difference.
        /// </summary>
        void Note(string label, string record)
        {
            var (count, records) = _found.GetValueOrDefault(label, (0, new Dictionary<string, int>(StringComparer.Ordinal)));
            records[record] = records.GetValueOrDefault(record) + 1;
            _found[label] = (count + 1, records);
        }

        /// <summary>
        /// A state's compared keys with their values as text or numbers, the entry's own handle and its use nodes left out.
        /// </summary>
        static Dictionary<string, object> Values(FcsRecord r)
        {
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var (k, v) in r.Bools) map[k] = v;
            foreach (var (k, v) in r.Ints) map[k] = v;
            foreach (var (k, v) in r.Floats) map[k] = v;
            foreach (var (k, v) in r.Strings) map[k] = v;
            foreach (string key in map.Keys.Where(k => Skipped().IsMatch(k)).ToList()) map.Remove(key);
            return map;
        }

        /// <summary>
        /// Whether two values agree: floats within a hundredth.
        /// </summary>
        static bool Same(object a, object b) => a is float x && b is float y ? MathF.Abs(x - y) <= 0.01f : a.Equals(b);

        /// <summary>
        /// Matches the keys of an entry's own handle, its stale null-handle parts and its use nodes.
        /// </summary>
        [GeneratedRegex(@"^(handle(TYPE|C|CS|I|S)|(residents|mounted)(CS|S)|usagenode_\d+)$")]
        private static partial Regex Skipped();
    }
}
