using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using SquadMemoryData = Meitou.Simulation.Dialogue.SquadMemoryData;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The wild platoons and nests of a capture.
/// </summary>
public static partial class SaveCapture
{
    /// <summary>
    /// The name prefix of a nest's GAMESTATE_TOWN state.
    /// </summary>
    const string NestStatePrefix = "Nest state ";
    /// <summary>
    /// The suffix of a GAMESTATE_TOWN state id (the hex of 94).
    /// </summary>
    const string TownStateSuffix = "-S5e";   // the hex of GAMESTATE_TOWN (94)

    /// <summary>
    /// The nests made so far in <c>quick.save</c>'s town list, the original's way (docs/formats/save.md "Nests"): a nest the save already holds keeps
    /// its entry and state, and gets the player's knowledge of it; a rolled one that has loaded (a rolled one that has not is left out: the layout
    /// rolls it again) gets an entry <c>&lt;n&gt;-quick.save-INGAME</c> (target the TOWN record, its position, a zero quaternion) and a
    /// GAMESTATE_TOWN <c>&lt;entry&gt;-S5e</c> named <c>Nest state &lt;name&gt; &lt;k&gt;</c> with a type-85 <c>hand</c> and the key set of the
    /// samples. The population remembers each one, so the next save reuses it. Returns each nest site's handle (what its platoons' <c>hometown</c>
    /// names).
    /// </summary>
    static Dictionary<int, Hand> WriteNests(SaveBuilder builder, World world, PopulationData data, PopulationSystem population, ulong seed)
    {
        var hands = new Dictionary<int, Hand>();
        var sites = population.WildSites;
        if (sites.Count == 0) return hands;

        var writer = new NestWriter(builder.Game, world, data, population, seed);
        foreach (var site in sites)
        {
            if (site.Kind != WildKind.Nest || site.Nest is not { } nest) continue;
            if (writer.Write(site, nest) is { } hand) hands[site.Index] = hand;
        }

        return hands;
    }

    /// <summary>
    /// Writes the nests to quick.save's town list: the entries, the states and the id counter shared by every nest of one save.
    /// </summary>
    sealed class NestWriter
    {
        /// <summary>
        /// The save being written.
        /// </summary>
        readonly SaveGame _game;

        /// <summary>
        /// Its quick.save.
        /// </summary>
        readonly SaveFile _quick;

        /// <summary>
        /// quick.save's records by string id.
        /// </summary>
        readonly Dictionary<string, FcsRecord> _byId;

        /// <summary>
        /// The town instance list (made with the first new nest when the save has none).
        /// </summary>
        FcsRecord? _list;

        /// <summary>
        /// The list's entries by id.
        /// </summary>
        readonly Dictionary<string, FcsInstance> _entries;

        /// <summary>
        /// The player's knowledge of each nest site.
        /// </summary>
        readonly Dictionary<int, CampView> _known;

        /// <summary>
        /// The highest town handle container; each new nest takes the next.
        /// </summary>
        int _container;

        /// <summary>
        /// The number after a nest state's name, counted on.
        /// </summary>
        int _counter;

        /// <summary>
        /// quick.save's id counter, taken once (a scan of every record) and counted on here; -1 before the first id.
        /// </summary>
        int _nextNumber = -1;

        /// <summary>
        /// The world saved.
        /// </summary>
        readonly World _world;

        /// <summary>
        /// The population data.
        /// </summary>
        readonly PopulationData _data;

        /// <summary>
        /// The population.
        /// </summary>
        readonly PopulationSystem _population;

        /// <summary>
        /// The save's seed (the nest handles' serials).
        /// </summary>
        readonly ulong _seed;

        /// <summary>
        /// Takes the save's lists and counters as they stand before any nest is written.
        /// </summary>
        public NestWriter(SaveGame game, World world, PopulationData data, PopulationSystem population, ulong seed)
        {
            (_world, _data, _population, _seed) = (world, data, population, seed);
            _game = game;
            _quick = _game.Quick;
            _byId = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
            foreach (var r in _quick.Records) _byId.TryAdd(r.StringId, r);
            _list = _game.TownInstanceList;
            _entries = new Dictionary<string, FcsInstance>(StringComparer.Ordinal);
            foreach (var e in _list?.Instances ?? []) _entries.TryAdd(e.Id, e);
            _known = new Dictionary<int, CampView>();
            foreach (var camp in _world.Player.Camps) _known[camp.Site] = camp;
            _container = _game.Towns.Select(t => t.Handle).Where(h => !h.IsNull).Select(h => h.C).DefaultIfEmpty(0).Max();

            // The number after a nest state's name: one more than the highest a save's nest has (its origin is Unknown, docs/formats/save.md
            // "Nests").
            _counter = _game.Towns.Select(t => t.Record.Name).Where(n => n.StartsWith(NestStatePrefix, StringComparison.Ordinal))
                .Select(n => int.TryParse(n[(n.LastIndexOf(' ') + 1)..], System.Globalization.CultureInfo.InvariantCulture, out int k) ? k : 0)
                .DefaultIfEmpty(_game.Towns.Count).Max();
        }

        /// <summary>
        /// The next id of quick.save.
        /// </summary>
        int Next()
        {
            if (_nextNumber < 0) _nextNumber = NextQuickNumber(_quick);
            else _quick.Data.NextId = ++_nextNumber;
            return _nextNumber;
        }

        /// <summary>
        /// One nest site: its state (the save's, else a new one), its pools and the player's knowledge of it; returns its handle, null for a
        /// nest not made so far.
        /// </summary>
        public Hand? Write(WildSite site, TownData nest)
        {
            // Only the nests made so far, as the original holds: the save's and the ones that have loaded. The others roll again at the same places
            // when the save loads.
            bool held = _population.NestSaveId(site.Index, out var id);
            if (!held && _population.WildCentreOf(site.Index) is null) return null;

            FcsRecord? state = null;
            if (held && _entries.TryGetValue(id.Instance, out var entry) && entry.States.Count > 0)
                state = _byId.GetValueOrDefault(entry.States[0]);

            state ??= NewState(site, nest);

            // Loaded once: `started` (Observed: set on most nests near the player's path, meaning not decoded).
            if (_population.WildCentreOf(site.Index) is not null) state.Bools["started"] = true;

            // Its pool (docs/simulation.md "Off-screen tier"): grown, spent, or wiped out.
            _population.NestPoolsOf(site.Index)?.Write(state);
            if (_known.TryGetValue(site.Index, out var camp))
            {
                if ((camp.Known & TownKnowledge.Discovered) != 0) state.Bools["discovered"] = true;
                if ((camp.Known & TownKnowledge.Explored) != 0) state.Bools["explored"] = true;
                state.Bools["recently discovered"] = (camp.Known & TownKnowledge.Recent) != 0;
            }

            return Hand.Read(state, "hand");
        }

        /// <summary>
        /// A new entry and GAMESTATE_TOWN for a nest that has loaded, remembered by the population; the town list is made first if needed.
        /// </summary>
        FcsRecord NewState(WildSite site, TownData nest)
        {
            var list = EnsureList();

            var centre = _population.WildCentreOf(site.Index) ?? site.Spots[0];
            var entry = new FcsInstance
            {
                Id = QuickNumberId(Next()),
                Target = nest.Id,
                Position = new Vector3(centre.X, _world.Walkability.GroundHeight(centre.X, centre.Y), centre.Y),
                Rotation = new Quaternion(0, 0, 0, 0),
            };
            var state = new FcsRecord
            {
                Type = (int)FcsRecordType.GamestateTown,
                Id = Next(),
                Name = $"{NestStatePrefix}{nest.Name} {++_counter}",
                StringId = entry.Id + TownStateSuffix,
            };
            entry.States.Add(state.StringId);
            list.Instances.Add(entry);
            _entries[entry.Id] = entry;

            // With the other town states, before the list (the original's order).
            int at = _quick.Records.IndexOf(list);
            _quick.Records.Insert(at < 0 ? _quick.Records.Count : at, state);
            _byId[state.StringId] = state;
            var hand = new Hand((int)FcsRecordType.Nest, ++_container, (int)Rng.Mix(Rng.StableHash(entry.Id) ^ _seed), 0, 0);
            hand.Write(state, "hand");
            NestKeys(state, site, nest);
            _game.Towns.Add(new SaveTown(state));
            _population.RememberNest(site.Index, entry.Id, hand);

            return state;
        }

        /// <summary>
        /// The town instance list, made when the save has none.
        /// </summary>
        FcsRecord EnsureList()
        {
            if (_list is not null) return _list;

            int n = Next();
            _list = new FcsRecord { Type = (int)FcsRecordType.GamestateTownInstanceList, Id = n, Name = "0", StringId = QuickNumberId(n) };
            _quick.Records.Add(_list);
            _game.TownInstanceList = _list;

            return _list;
        }

        /// <summary>
        /// A new nest state's keys: not started, explored or discovered, its faction, and the pools as a full nest of the samples has them.
        /// </summary>
        void NestKeys(FcsRecord state, WildSite site, TownData nest)
        {
            state.Ints["artifacts count"] = 0;
            foreach (var k in new[] { "started", "explored", "recently discovered", "discovered", "ded" })
                state.Bools[k] = false;

            state.Bools["is nest"] = true;
            state.Strings["faction"] = site.Faction >= 0 ? _data.Factions[site.Faction].Id : nest.Faction ?? "";

            // The pool keys as a full nest of the samples has them (popd2 = pop2 = nest resident population, town pools 0); its pool overwrites them
            // below.
            state.Floats["popd2"] = nest.NestResidentPopulation;
            state.Floats["pop2"] = nest.NestResidentPopulation;
            foreach (var k in new[] { "popd", "pop", "tod" })
                state.Floats[k] = 0;
        }
    }

    /// <summary>
    /// The wild squads the population made that are in the world (docs/formats/save.md "Wild and nest platoons") as new platoons: a nest's with
    /// <c>basetown</c> its TOWN record and <c>hometown</c> the nest's handle, a homeless one with neither; <c>sqt</c> 2 (every wild platoon of the
    /// samples), the region as <c>map area</c>, the package running; its characters as new ones. Loaded again they are the save's wild platoons,
    /// counted by their nest or sector.
    /// </summary>
    static void WriteWildNpcs(SaveBuilder builder, World world, PopulationData data, PopulationSystem population, Dictionary<int, Hand> nests,
        SaveCaptureOptions options, SaveClock clock, IReadOnlyDictionary<string, FcsRecord> buildings, Dictionary<string, RaceData?> races)
    {
        var writer = new WildPlatoonWriter(builder, world, data, nests, options, clock, buildings, races, FactionsOf(builder.Game));
        WriteSiteSquads(world, data, population, writer);
        WriteStraySquads(world, data, population, writer);
    }

    /// <summary>
    /// The wild squads of each site in the world, and the ones kept while their site is unloaded.
    /// </summary>
    static void WriteSiteSquads(World world, PopulationData data, PopulationSystem population, WildPlatoonWriter writer)
    {
        var table = world.Characters;
        foreach (var site in population.WildSites)
        {
            foreach (int squadId in population.WildSquadsOf(site.Index))
            {
                if (world.Squads.Find(squadId) is not { } squad || squad.Faction < 0 || squad.Faction >= data.Factions.Count) continue;

                // The dead stay in the platoon's list as the original writes it (FUN_1404ff410, docs/game/character-stats.md "Corpses").
                var every = squad.Members.Where(m => table.IsAlive(m) && table.Cold(m.Slot) is { IsPlayer: false, Save: null }).ToList();
                var alive = every.Where(m => table.Cold(m.Slot)!.DiedTick < 0).ToList();
                if (alive.Count == 0) continue;

                var leader = alive.Contains(squad.Leader) ? squad.Leader : alive[0];
                writer.Write(site, squad.TemplateId, squad.Faction, squad.Money, squad.Ai?.Current?.Id,
                    [.. every.Select(id => (table.Cold(id.Slot)!, table.Previous[id.Slot].Position, table.Previous[id.Slot].Yaw, id, id == leader))],
                    MemoryOf(world)?.Snapshot(squad.Id));
            }

            // Kept while the site is unloaded (the Meitou KeepWild switch): their characters as they were stored.
            foreach (var k in population.KeptWildOf(site.Index))
            {
                if (k.Faction < 0 || k.Faction >= data.Factions.Count || k.Stored.Members.Count == 0) continue;

                bool hasLeader = k.Stored.Members.Any(m => m.Leader);
                writer.Write(site, k.TemplateId, k.Faction, k.Money, k.Stored.Package,
                    [.. k.Stored.Members.Select((m, i) => (m.Cold, m.Position, m.Yaw, CharacterId.None, m.Leader || !hasLeader && i == 0))],
                        k.Stored.Memory);
            }
        }
    }

    /// <summary>
    /// The NPC squads of no town, platoon or wild site, written homeless and persistent (see the comment below).
    /// </summary>
    static void WriteStraySquads(World world, PopulationData data, PopulationSystem population, WildPlatoonWriter writer)
    {
        var table = world.Characters;

        // NPC squads of no town, platoon or wild site: a dismissed character's "Wandering Squad" (the original's FUN_14048fa30 makes it a persistent
        // platoon, which saveGame writes like any other), a slave bought free or handed over, one separated by dialogue. Written homeless and
        // persistent; loaded again they are homeless wild platoons.
        var wild = new HashSet<int>();
        foreach (var site in population.WildSites) wild.UnionWith(population.WildSquadsOf(site.Index));
        foreach (var squad in world.Squads.All)
        {
            if (squad.Town >= 0 || squad.PlatoonId >= 0 || squad.Dead || wild.Contains(squad.Id) || squad.Faction == world.Player.Faction
                || squad.Faction < 0 || squad.Faction >= data.Factions.Count)
                continue;

            var every = squad.Members.Where(m => table.IsAlive(m) && table.Cold(m.Slot) is { IsPlayer: false, Save: null }).ToList();
            var alive = every.Where(m => table.Cold(m.Slot)!.DiedTick < 0).ToList();
            if (alive.Count == 0) continue;

            var leader = alive.Contains(squad.Leader) ? squad.Leader : alive[0];
            writer.Write(null, squad.TemplateId, squad.Faction, squad.Money, squad.Ai?.Current?.Id,
                [.. every.Select(id => (table.Cold(id.Slot)!, table.Previous[id.Slot].Position, table.Previous[id.Slot].Yaw, id, id == leader))],
                MemoryOf(world)?.Snapshot(squad.Id), persistent: true);
        }
    }

    /// <summary>
    /// Writes wild platoons with their characters, each named afresh in its faction.
    /// </summary>
    sealed class WildPlatoonWriter(SaveBuilder builder, World world, PopulationData data, Dictionary<int, Hand> nests, SaveCaptureOptions options,
        SaveClock clock, IReadOnlyDictionary<string, FcsRecord> buildings, Dictionary<string, RaceData?> races,
        Dictionary<string, SaveFaction> factions)
    {
        /// <summary>
        /// A platoon of the wild <paramref name="site"/> (none: homeless) with its <paramref name="members"/>.
        /// </summary>
        public void Write(WildSite? site, string templateId, int factionIndex, long money, string? package,
            List<(CharacterCold Cold, Vector3 Position, float Yaw, CharacterId Id, bool Leader)> members, SquadMemoryData? memory = null,
            bool persistent = false)
        {
            var faction = data.Factions[factionIndex];
            factions.TryGetValue(faction.Id, out var state);
            var at = members.First(m => m.Leader).Position;
            int platoonMoney = (int)Math.Clamp(money, int.MinValue, int.MaxValue);
            var platoon = builder.AddPlatoon(FreshName(builder.Game, state, faction.Name), faction.Name, faction.Id, templateId, at, platoonMoney);
            var record = platoon.Record;
            record.Ints["sqt"] = 2;

            // Not persistent, but while it occupies a town (the original's occupation makes its platoon persistent, docs/game/ai.md "Package
            // classes").
            record.Bools["persistent"] = persistent || Ai.AiSystem.Occupies(data.Ai, package);
            if (site is { Kind: WildKind.Nest, Nest: { } nest } && nests.TryGetValue(site.Index, out var hand))
            {
                record.Strings["basetown"] = nest.Id;
                hand.Write(record, "hometown");
            }

            WriteMapArea(record, data, at);
            if (package is not null) record.Strings["currentPackage"] = package;
            NoteMemory(record, memory);
            foreach (var m in members)
            {
                int f = m.Cold.Faction;
                string owner = f >= 0 && f < data.Factions.Count ? data.Factions[f].Id : faction.Id;
                WriteCharacter(builder, world, data, options, clock, buildings, races, platoon, null, m.Cold, m.Position, m.Yaw, m.Id, owner,
                    m.Leader, orders: false);
            }

            SetCharCount(platoon);
        }
    }

    /// <summary>
    /// The save's faction states by id (the names their platoons are given).
    /// </summary>
    static Dictionary<string, SaveFaction> FactionsOf(SaveGame game)
    {
        var factions = new Dictionary<string, SaveFaction>(StringComparer.Ordinal);
        foreach (var f in game.Factions) factions.TryAdd(f.Id, f);

        return factions;
    }

    /// <summary>
    /// A new platoon's region: <c>map area sid</c> = the BIOME_GROUP of the cell it stands in, <c>map area name</c> its name (as every platoon of the
    /// samples has).
    /// </summary>
    static void WriteMapArea(FcsRecord record, PopulationData data, Vector3 at)
    {
        if (data.Biomes is not { } biomes) return;

        var (cx, cz) = WeatherAreas.CellOf(at.X, at.Z);
        int g = biomes.GroupOf(cx, cz);
        if (g < 0) return;

        record.Strings["map area sid"] = biomes.Groups[g].Id;
        record.Strings["map area name"] = biomes.Groups[g].Name;
    }

    /// <summary>
    /// The id of number <paramref name="n"/> in <c>quick.save</c>.
    /// </summary>
    static string QuickNumberId(int n) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{n}-{SaveFolder.QuickSaveName}-INGAME");

    /// <summary>
    /// Takes the next number of <c>quick.save</c>'s id counter.
    /// </summary>
    static int NextQuickNumber(SaveFile file)
    {
        int n = Math.Max(file.Data.NextId, file.Records.Count == 0 ? 0 : file.Records.Max(r => Math.Max(r.Id, SaveGame.NumberOf(r)))) + 1;
        file.Data.NextId = n;
        return n;
    }
}
