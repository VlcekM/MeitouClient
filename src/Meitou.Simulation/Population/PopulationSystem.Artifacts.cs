using System.Globalization;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Economy;
using Meitou.Data.World;
using Meitou.Simulation.Items;

namespace Meitou.Simulation;

/// <summary>
/// The towns' artifacts (docs/game/economy.md "Artifacts"): each town's list, made for a new game (<see cref="Artifacts.Plan"/>) or read from a loaded save's town states, and given
/// out into the town's buildings when it makes its residents.
/// </summary>
public sealed partial class PopulationSystem
{
    /// <summary>Each site's artifacts still to be given out (null until <see cref="EnsureArtifacts"/>).</summary>
    List<ArtifactStack>[]? townArtifacts;
    PriceConstants? prices;

    /// <summary>The artifacts still to be given out at <paramref name="site"/>: empty once given, or for none.</summary>
    public IReadOnlyList<ArtifactStack> ArtifactsOf(World world, int site)
    {
        EnsureArtifacts(world);
        return (uint)site < (uint)townArtifacts!.Length ? townArtifacts[site] : [];
    }

    /// <summary>The lists, once: a loaded save's town states, else (a new game) the towns' share of the data's artifacts.</summary>
    void EnsureArtifacts(World world)
    {
        if (townArtifacts is not null) return;
        townArtifacts = new List<ArtifactStack>[data.Sites.Count];
        for (int i = 0; i < townArtifacts.Length; i++) townArtifacts[i] = [];
        if (world.Saved is { } saved)
        {
            foreach (var site in data.Sites)
                if (site.InstanceId.Length > 0 && saved.TownStates.TryGetValue(site.InstanceId, out var state))
                    townArtifacts[site.Index] = ReadArtifacts(state, data.Db);
            return;
        }
        foreach (var (site, list) in PlanArtifacts(data)) townArtifacts[site] = list;
    }

    /// <summary>The data's artifacts shared among the placed towns, by site index.</summary>
    internal static Dictionary<int, List<ArtifactStack>> PlanArtifacts(PopulationData data) =>
        Artifacts.Plan(data.Db, data.Sites.Select(s => new ArtifactTown(s.Index, s.Record, s.Position)));

    const string ArtifactCountKey = "artifacts count", ArtifactPrefix = "artifact_";

    static string Key(int k, string field) => ArtifactPrefix + k.ToString(CultureInfo.InvariantCulture) + "_" + field;

    /// <summary>A town state's list (<c>artifacts count</c>, <c>artifact_&lt;k&gt;_itm/mat/man/lvl/qty</c>): the entries whose item the data has; a part it lacks is none.</summary>
    internal static List<ArtifactStack> ReadArtifacts(FcsRecord state, GameDatabase db)
    {
        var list = new List<ArtifactStack>();
        int count = state.Ints.GetValueOrDefault(ArtifactCountKey);
        for (int k = 0; k < count; k++)
        {
            string item = state.Strings.GetValueOrDefault(Key(k, "itm"), "");
            if (db.Find(item) is null) continue;
            string Part(string field) => state.Strings.GetValueOrDefault(Key(k, field), "") is var id && db.Find(id) is not null ? id : "";
            list.Add(new ArtifactStack(item, Part("mat"), Part("man"), state.Ints.GetValueOrDefault(Key(k, "lvl")), state.Ints.GetValueOrDefault(Key(k, "qty"))));
        }
        return list;
    }

    /// <summary>Writes <paramref name="list"/> as the town state's list, replacing the one it had.</summary>
    internal static void WriteArtifacts(FcsRecord state, IReadOnlyList<ArtifactStack> list)
    {
        foreach (var key in state.Strings.Keys.Where(k => k.StartsWith(ArtifactPrefix, StringComparison.Ordinal)).ToList()) state.Strings.Remove(key);
        foreach (var key in state.Ints.Keys.Where(k => k.StartsWith(ArtifactPrefix, StringComparison.Ordinal)).ToList()) state.Ints.Remove(key);
        state.Ints[ArtifactCountKey] = list.Count;
        for (int k = 0; k < list.Count; k++)
        {
            var s = list[k];
            state.Strings[Key(k, "itm")] = s.Item;
            state.Strings[Key(k, "mat")] = s.Part1;
            state.Strings[Key(k, "man")] = s.Part2;
            state.Ints[Key(k, "lvl")] = s.Level;
            state.Ints[Key(k, "qty")] = s.Units;
        }
    }

    /// <summary>An artifact made for the distribution: its value and the item.</summary>
    readonly record struct MadeArtifact(uint Value, ItemInstance Item);

    /// <summary>
    /// Gives a town's artifacts out into the buildings it housed residents in, as the original's distribution (docs/game/economy.md "Artifacts"); a town with no building that takes
    /// them keeps its list. Engine choices: ties and the buildings' turns follow the town's building order, the candidates are those Meitou housed a template in, and what goes to a
    /// building whose interior the level data does not name (the original generates one, Meitou has none) is lost.
    /// </summary>
    void GiveArtifacts(World world, BuiltTown town)
    {
        EnsureArtifacts(world);
        var list = townArtifacts![town.Site];
        if (list.Count == 0 || town.Homes.Count == 0) return;
        int player = world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;
        if (player >= 0 && town.Site < data.SiteFactions.Count && data.SiteFactions[town.Site] == player) return;
        string? playerId = player >= 0 && player < data.Factions.Count ? data.Factions[player].Id : null;
        var site = data.Sites[town.Site];

        var templateOf = new Dictionary<TownBuilding, SquadTemplate>(ReferenceEqualityComparer.Instance);
        foreach (var (b, t) in town.Homes) templateOf.TryAdd(b, t);
        var itemHomes = new List<(uint Base, TownBuilding Building)>();
        var gearHomes = new List<(uint Base, TownBuilding Building)>();
        foreach (var b in site.Buildings)
        {
            if (!b.Furnished || playerId is not null && b.OwnerFactionId == playerId || world.Fixtures.OwnsBuilding(b.Key, player) || !templateOf.TryGetValue(b, out var t)) continue;
            if (t.ItemArtifactsBaseValue != 0) itemHomes.Add(((uint)t.ItemArtifactsBaseValue, b));
            if (t.GearArtifactsBaseValue != 0) gearHomes.Add(((uint)t.GearArtifactsBaseValue, b));
        }
        if (itemHomes.Count == 0 && gearHomes.Count == 0) return;
        itemHomes = [.. itemHomes.OrderByDescending(h => h.Base)];
        gearHomes = [.. gearHomes.OrderByDescending(h => h.Base)];

        prices ??= PriceConstants.FromDatabase(data.Db);
        var items = new List<MadeArtifact>();
        var gear = new List<MadeArtifact>();
        foreach (var s in list)
        {
            uint value = (uint)Artifacts.ValueOf(data.Db, s, prices);
            for (int u = 0; u < s.Units; u++)
                if (MakeArtifact(s) is { } item) (item.Type == FcsRecordType.ITEM ? items : gear).Add(new MadeArtifact(value, item));
        }
        list.Clear();

        var rng = new BoostRandom(Artifacts.Seed);
        var given = new Dictionary<TownBuilding, List<MadeArtifact>>(ReferenceEqualityComparer.Instance);
        // Each item to a building drawn among the first ones worth at least as much; with none it is lost.
        void Share(List<MadeArtifact> made, List<(uint Base, TownBuilding Building)> homes)
        {
            foreach (var entry in made.OrderByDescending(m => m.Value))
            {
                int worth = homes.FindIndex(h => entry.Value > h.Base);
                int last = (worth < 0 ? homes.Count : worth) - 1;
                if (last < 0) continue;
                var home = homes[rng.UniformInt(0, last)].Building;
                (given.TryGetValue(home, out var mine) ? mine : given[home] = []).Add(entry);
            }
        }
        Share(items, itemHomes);
        Share(gear, gearHomes);

        // Within a building: its stock containers emptied, strongest lock first; each item into one of the first 1, 2, 4... drawn, else the others in a shuffled order.
        var shuffler = rng.Clone();
        foreach (var b in site.Buildings)
        {
            if (!given.TryGetValue(b, out var mine)) continue;
            var boxes = new List<(int Lock, Locks.Fixture Box)>();
            foreach (var f in b.Fixtures)
                if (f.HoldsShopStock && world.Fixtures.Find(f.Id) is { Contents: { } grid } box)
                {
                    grid.Items.Clear();
                    boxes.Add((box.Lock?.Level ?? 0, box));
                }
            if (boxes.Count == 0) continue;
            boxes = [.. boxes.OrderByDescending(x => x.Lock)];
            int limit = 1;
            foreach (var made in mine.OrderByDescending(m => m.Value))
            {
                int pick = rng.UniformInt(0, limit - 1);
                if (!boxes[pick].Box.Contents!.AddTo("main", made.Item))
                {
                    var order = Enumerable.Range(0, boxes.Count).ToList();
                    shuffler.Shuffle(order);
                    foreach (int k in order)
                        if (k != pick && boxes[k].Box.Contents!.AddTo("main", made.Item)) break;
                }
                limit = Math.Min(limit * 2, boxes.Count);
            }
            foreach (var (_, box) in boxes)
                if (box.Contents!.Items.Count > 0 && box.Lock is { } l)
                {
                    l.Locked = true;
                    world.Fixtures.Touch(box);
                }
        }
    }

    /// <summary>One unit of an entry as an item flagged unique: armour at its level as quality, a weapon of its maker and model, a crossbow or limb at its level.</summary>
    ItemInstance? MakeArtifact(ArtifactStack s)
    {
        if (data.Db.Find(s.Item) is not { } record || data.Items.Create(s.Item, 1, record.Type == FcsRecordType.ARMOUR ? s.Level : 0) is not { } item) return null;
        if (record.Type == FcsRecordType.WEAPON)
        {
            item.CompanyId = s.Part2;
            item.MaterialId = s.Part1;
            item.Level = s.Level;
        }
        else if (record.Type is FcsRecordType.CROSSBOW or FcsRecordType.LIMB_REPLACEMENT) item.Level = s.Level;
        item.Unique = true;
        return item;
    }

    /// <summary>The lists while any town has artifacts left (so worlds without hash as before).</summary>
    void HashArtifacts(ref StateHasher h)
    {
        if (townArtifacts is null || townArtifacts.All(l => l.Count == 0)) return;
        for (int site = 0; site < townArtifacts.Length; site++)
        {
            if (townArtifacts[site].Count == 0) continue;
            h.Add(site);
            h.Add(townArtifacts[site].Count);
            foreach (var s in townArtifacts[site])
            {
                h.Add(Rng.StableHash(s.Item));
                h.Add(Rng.StableHash(s.Part1));
                h.Add(Rng.StableHash(s.Part2));
                h.Add(s.Level);
                h.Add(s.Units);
            }
        }
    }
}
