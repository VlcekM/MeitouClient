using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Economy;

/// <summary>
/// One entry of a town's artifacts (docs/game/economy.md "Artifacts"): the item record, part 1 (a weapon's model) and part 2 (its maker), "" for
/// none, the level and the units.
/// </summary>
public readonly record struct ArtifactStack(string Item, string Part1, string Part2, int Level, int Units);

/// <summary>
/// A placed town the artifacts may go to; <paramref name="Key"/> is the caller's (a site index).
/// </summary>
public readonly record struct ArtifactTown(int Key, TownData Town, Vector3 Position);

/// <summary>
/// The towns' artifacts of a new game or an import, and an artifact's value (docs/game/economy.md "Artifacts").
/// </summary>
public static class Artifacts
{
    /// <summary>
    /// The seed of the gear spread and of every town's distribution.
    /// </summary>
    public const uint Seed = 11667085;

    /// <summary>
    /// The <see cref="ItemValue"/> of an entry at its level without the sale multipliers, a weapon with its maker's <c>price mod</c>; 0 when the data
    /// lacks the item.
    /// </summary>
    public static int ValueOf(GameDatabase db, ArtifactStack stack, PriceConstants prices)
    {
        if (db.Find(stack.Item) is not { } record) return 0;
        var maker = record.Type == FcsRecordType.Weapon ? db.Find(stack.Part2) : null;
        return ItemValue.Of(db, record, stack.Level, false, prices, maker);
    }

    /// <summary>
    /// An artifact being handed out: its stack, its value, the units still to give and the indexes of the towns whose range holds its value.
    /// </summary>
    sealed class Entry(ArtifactStack stack, int value)
    {
        /// <summary>
        /// The item, its parts, level and units as listed.
        /// </summary>
        public ArtifactStack Stack { get; } = stack;

        /// <summary>
        /// The <see cref="ValueOf"/> of one unit.
        /// </summary>
        public int Value { get; } = value;

        /// <summary>
        /// The units not given to a town yet.
        /// </summary>
        public int Left { get; set; } = stack.Units;

        /// <summary>
        /// The indexes (into the taker list) of the towns that accept its value, in the takers' order.
        /// </summary>
        public List<int> Towns { get; } = [];
    }

    /// <summary>
    /// A town taking items or gear, with the value range it accepts and what it has been given.
    /// </summary>
    sealed class Taker(int key, TownData town, Vector3 position, uint min, uint max)
    {
        /// <summary>
        /// The caller's key of the town (<see cref="ArtifactTown.Key"/>).
        /// </summary>
        public int Key { get; } = key;

        /// <summary>
        /// The TOWN record.
        /// </summary>
        public TownData Town { get; } = town;

        /// <summary>
        /// The town's position; the last tie-breaks of the town order.
        /// </summary>
        public Vector3 Position { get; } = position;

        /// <summary>
        /// The lowest value it takes (the town's artifacts min value).
        /// </summary>
        public uint Min { get; } = min;

        /// <summary>
        /// The highest value it takes (the town's artifacts max value).
        /// </summary>
        public uint Max { get; } = max;

        /// <summary>
        /// The stacks given so far with their unit value, one per value, item and parts.
        /// </summary>
        public List<(ArtifactStack Stack, int Value)> Given { get; } = [];

        /// <summary>
        /// Whether <paramref name="value"/> lies in [<see cref="Min"/>, <see cref="Max"/>] (compared unsigned).
        /// </summary>
        public bool Accepts(int value) => Min <= (uint)value && (uint)value <= Max;

        /// <summary>
        /// Moves <paramref name="units"/> of the entry here, onto the entry of the same value, item and parts it already has.
        /// </summary>
        public void Add(Entry e, int units)
        {
            e.Left -= units;
            for (int i = 0; i < Given.Count; i++)
            {
                var (s, v) = Given[i];
                if (v == e.Value && s.Item == e.Stack.Item && s.Part1 == e.Stack.Part1 && s.Part2 == e.Stack.Part2)
                {
                    Given[i] = (s with { Units = s.Units + units }, v);
                    return;
                }
            }
            Given.Add((e.Stack with { Units = units }, e.Value));
        }
    }

    /// <summary>
    /// The artifacts each town takes, by <see cref="ArtifactTown.Key"/>: its gear, then its items. Empty when the data has no ARTIFACTS record.
    /// </summary>
    public static Dictionary<int, List<ArtifactStack>> Plan(GameDatabase db, IEnumerable<ArtifactTown> towns)
    {
        var result = new Dictionary<int, List<ArtifactStack>>();
        if (db.OfType(FcsRecordType.Artifacts).FirstOrDefault() is not { } record) return result;

        var items = new List<Entry>();
        var gear = new List<Entry>();
        ReadEntries(db, record, items, gear);

        var itemTowns = new List<Taker>();
        var gearTowns = new List<Taker>();
        ReadTakers(towns, itemTowns, gearTowns);

        var townOrder = Comparer<Taker>.Create(CompareTowns);
        var entryOrder = Comparer<Entry>.Create(CompareEntries);
        itemTowns = [.. itemTowns.Order(townOrder)];
        gearTowns = [.. gearTowns.Order(townOrder)];
        items = [.. items.Order(entryOrder)];
        gear = [.. gear.Order(entryOrder)];
        FindTowns(itemTowns, items);
        FindTowns(gearTowns, gear);

        SpreadGear(gearTowns, gear);
        SpreadItems(itemTowns, items);

        foreach (var t in gearTowns.Concat(itemTowns))
        {
            if (t.Given.Count == 0) continue;
            if (!result.TryGetValue(t.Key, out var list)) result[t.Key] = list = [];
            list.AddRange(t.Given.Select(g => g.Stack));
        }
        return result;
    }

    /// <summary>
    /// The ARTIFACTS record's entries with their values: <c>items</c> into <paramref name="items"/>, the armours, crossbows, robotics and weapons
    /// into <paramref name="gear"/>.
    /// </summary>
    static void ReadEntries(GameDatabase db, GameRecord record, List<Entry> items, List<Entry> gear)
    {
        var prices = PriceConstants.FromDatabase(db);
        void Add(List<Entry> into, ArtifactStack s)
        {
            // A gear entry of 0 units or an item entry of fewer never ends the original's spread; both are left out.
            if (s.Units > 0) into.Add(new Entry(s, ValueOf(db, s, prices)));
        }

        // The units of these lists are read by the entry's position among the entries whose record exists.
        foreach (var (list, level, into) in new[] { ("items", 0, items), ("armours", 80, gear), ("armours hq", 95, gear) })
        {
            var refs = record.GetReferences(list);
            var found = Existing(db, refs);
            for (int i = 0; i < found.Count; i++)
                Add(into, new ArtifactStack(found[i], "", "", level, refs[i].Values.Value0));
        }
        foreach (var list in new[] { "crossbows", "robotics" })
        {
            foreach (var r in record.GetReferences(list))
            {
                if (db.Find(r.TargetStringId) is null) continue;
                if (r.Values.Value0 > 0) Add(gear, new ArtifactStack(r.TargetStringId, "", "", 95, r.Values.Value0));
                if (r.Values.Value1 > 0) Add(gear, new ArtifactStack(r.TargetStringId, "", "", 80, r.Values.Value1));
            }
        }
        AddWeapons(db, record, gear, Add);
    }

    /// <summary>
    /// The towns that take items and those that take gear (each with a maximum value set), nests left out.
    /// </summary>
    static void ReadTakers(IEnumerable<ArtifactTown> towns, List<Taker> itemTowns, List<Taker> gearTowns)
    {
        foreach (var t in towns)
        {
            if (t.Town.Type is TownType.Nest or TownType.NestMarker or TownType.Null) continue;
            if (t.Town.ItemArtifactsMaxValue != 0)
                itemTowns.Add(new Taker(t.Key, t.Town, t.Position, (uint)t.Town.ItemArtifactsMinValue, (uint)t.Town.ItemArtifactsMaxValue));
            if (t.Town.GearArtifactsMaxValue != 0)
                gearTowns.Add(new Taker(t.Key, t.Town, t.Position, (uint)t.Town.GearArtifactsMinValue, (uint)t.Town.GearArtifactsMaxValue));
        }
    }

    /// <summary>
    /// Gear: one unit of the first artifact each town takes, then the rest unit by unit to towns drawn from each artifact's.
    /// </summary>
    static void SpreadGear(List<Taker> gearTowns, List<Entry> gear)
    {
        var rng = new BoostRandom(Seed);
        foreach (var t in gearTowns)
        {
            for (int k = 0; k < gear.Count; k++)
            {
                if (!t.Accepts(gear[k].Value)) continue;
                t.Add(gear[k], 1);
                if (gear[k].Left == 0) gear.RemoveAt(k);
                break;
            }
        }
        foreach (var e in gear)
            while (e.Towns.Count > 0 && e.Left > 0) gearTowns[e.Towns[rng.UniformInt(0, e.Towns.Count - 1)]].Add(e, 1);
    }

    /// <summary>
    /// Items: spread over their towns in order by the towns' maxima.
    /// </summary>
    static void SpreadItems(List<Taker> itemTowns, List<Entry> items)
    {
        foreach (var e in items)
        {
            if (e.Towns.Count == 0) continue;
            int total = 0;
            foreach (int k in e.Towns) total += (int)itemTowns[k].Max;
            float average = (float)total / e.Left;
            float balance = -1;
            int at = -1, run = 0;
            for (int n = e.Left; n > 0; n--)
            {
                if (balance < 0)
                {
                    if (run > 0) itemTowns[e.Towns[at]].Add(e, run);
                    run = 0;
                    // The original reads past its towns when their maxima add up to less than the units (or rounding leaves the balance negative);
                    // Meitou stays on the last.
                    at = Math.Min(at + 1, e.Towns.Count - 1);
                    balance += itemTowns[e.Towns[at]].Max;
                }
                run++;
                balance -= average;
            }
            if (run > 0) itemTowns[e.Towns[at]].Add(e, run);
        }
    }

    /// <summary>
    /// The ids of the entries of <paramref name="refs"/> whose record exists, in order.
    /// </summary>
    static List<string> Existing(GameDatabase db, IReadOnlyList<GameReference> refs) =>
        [.. refs.Where(r => db.Find(r.TargetStringId) is not null).Select(r => r.TargetStringId)];

    /// <summary>
    /// <c>weapons</c>: each model's maker is the WEAPON_MANUFACTURER other than <c>PLAYER_WEAPONS</c> that lists it in <c>weapon models</c> (the last
    /// in data order), and each WEAPON of its <c>weapon types</c> is an artifact of the model, the maker and the maker's v0 for the model as level.
    /// </summary>
    static void AddWeapons(GameDatabase db, GameRecord record, List<Entry> gear, Action<List<Entry>, ArtifactStack> add)
    {
        var refs = record.GetReferences("weapons");
        var models = Existing(db, refs);
        if (models.Count == 0) return;
        var makerOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var levelOf = new Dictionary<(string Maker, string Model), int>();
        var typesOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var maker in db.OfType(FcsRecordType.WeaponManufacturer))
        {
            if (maker.StringId == "PLAYER_WEAPONS") continue;
            var modelRefs = maker.GetReferences("weapon models");
            var made = Existing(db, modelRefs);
            for (int j = 0; j < made.Count; j++)
            {
                makerOf[made[j]] = maker.StringId;
                levelOf[(maker.StringId, made[j])] = (ushort)modelRefs[j].Values.Value0;
            }
            foreach (var type in Existing(db, maker.GetReferences("weapon types")))
                (typesOf.TryGetValue(maker.StringId, out var types) ? types : typesOf[maker.StringId] = []).Add(type);
        }
        for (int i = 0; i < models.Count; i++)
        {
            if (!makerOf.TryGetValue(models[i], out var maker) || !typesOf.TryGetValue(maker, out var types)) continue;
            int level = levelOf[(maker, models[i])];
            foreach (var type in types)
                add(gear, new ArtifactStack(type, models[i], maker, level, refs[i].Values.Value0));
        }
    }

    /// <summary>
    /// Gives each artifact the towns whose range holds its value (none when it lies outside every range).
    /// </summary>
    static void FindTowns(List<Taker> towns, List<Entry> entries)
    {
        uint lowest = uint.MaxValue, highest = 0;
        foreach (var t in towns)
        {
            lowest = Math.Min(lowest, t.Min);
            highest = Math.Max(highest, t.Max);
        }
        foreach (var e in entries)
        {
            if ((uint)e.Value > highest || (uint)e.Value < lowest) continue;
            for (int k = 0; k < towns.Count; k++)
                if (towns[k].Accepts(e.Value)) e.Towns.Add(k);
        }
    }

    /// <summary>
    /// The takers' order: by max value, min value, TOWN id, then position X and Z.
    /// </summary>
    static int CompareTowns(Taker a, Taker b)
    {
        if (a.Max != b.Max) return a.Max.CompareTo(b.Max);
        if (a.Min != b.Min) return a.Min.CompareTo(b.Min);
        int c = string.CompareOrdinal(a.Town.Id, b.Town.Id);
        if (c != 0) return c;
        if (a.Position.X != b.Position.X) return a.Position.X.CompareTo(b.Position.X);
        return a.Position.Z.CompareTo(b.Position.Z);
    }

    /// <summary>
    /// The artifacts' order: by value (unsigned), then item, part 1 and part 2 ordinally (one with the record before one without), then level.
    /// </summary>
    static int CompareEntries(Entry a, Entry b)
    {
        if (a.Value != b.Value) return ((uint)a.Value).CompareTo((uint)b.Value);
        foreach (var (x, y) in new[] { (a.Stack.Item, b.Stack.Item), (a.Stack.Part1, b.Stack.Part1), (a.Stack.Part2, b.Stack.Part2) })
        {
            // One with the record before one without; two without compare equal from here on.
            if (x.Length == 0 || y.Length == 0) return x.Length == 0 ? (y.Length == 0 ? 0 : 1) : -1;
            int c = string.CompareOrdinal(x, y);
            if (c != 0) return c;
        }
        return a.Stack.Level.CompareTo(b.Stack.Level);
    }
}