using SquadMemoryData = Meitou.Simulation.Dialogue.SquadMemoryData;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Saving;

/// <summary>Settings of <see cref="SaveCapture.Capture"/>.</summary>
public sealed class SaveCaptureOptions
{
    /// <summary>The data files in load order, for the CAMERA <c>mods</c> list.</summary>
    public IReadOnlyList<string> DataFiles { get; init; } = LoadOrder.BaseFiles;

    /// <summary>The placed towns of the level data, in order. A new save (no loaded one to carry on) writes a default state for each; without them it has no town states.</summary>
    public IReadOnlyList<TownPlacement>? Placements { get; init; }

    /// <summary>Supplies the stats and medical state of a character when it has none from a loaded save (the host's body systems). Null results fall back to the saved ones, then to defaults.</summary>
    public Func<CharacterId, (CharacterStats? Stats, MedicalState? Medical)>? Bodies { get; init; }

    /// <summary>Seeds the random serials of new handles.</summary>
    public ulong Seed { get; init; }

    /// <summary>
    /// The level data: the zone files of the visited zones a loaded save has none of are made from it (<see cref="ZoneSave"/>), and a new save's town states take their level state's
    /// handle. Null: no zone files are made and new towns get new handles.
    /// </summary>
    public WorldLevelData? Levels { get; init; }

    /// <summary>The strategy camera's view, written into CAMERA <c>pos</c> / <c>rot</c> / <c>zoom</c> (docs/formats/camera.md "Saved camera"). Null: the record keeps what it had.</summary>
    public SavedView? View { get; init; }
}

/// <summary>A strategy-camera view: the pivot on the ground, yaw (0 looks along -Z), pitch (radians below the horizon) and boom length.</summary>
public readonly record struct SavedView(Vector3 Pivot, float Yaw, float Pitch, float Distance);

/// <summary>
/// Turns a <see cref="World"/> into a <see cref="SaveGame"/> (docs/simulation.md "Saves"). With the <see cref="LoadedSave"/> the world was loaded from, the save it came
/// from is copied and the parts the world models are written over it: clock, money, the factions' relations and prosperity, the player's platoons and their characters (position,
/// facing, stats, medical state, new recruits, removals), the selection, the save's NPC platoons (stand-in positions; the characters of those that were in the world) and the
/// population's own town and roaming squads in the world as new platoons; everything else is carried through unchanged. Without one a new save
/// is made from the game data: all factions with their relations, the player's platoons and the population's squads, a default state per placed town, and empty weather, research and decals.
/// Either way every visited zone gets a zone file (<see cref="ZoneSave"/>).
/// </summary>
public static partial class SaveCapture
{
    /// <summary>
    /// The save a capture starts from: a copy of the loaded save, or a new one made from the data (all factions with the data's relations, a default state per placed town). It holds
    /// nothing of the world, and costs tens of milliseconds, so a host makes it ahead on a worker and hands it to <see cref="Capture"/> (which changes it: one use). Reads
    /// <paramref name="data"/>'s relations and the loaded save, neither of which a running world changes.
    /// </summary>
    public static SaveBuilder PrepareBase(PopulationData data, LoadedSave? loaded = null, SaveCaptureOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        options ??= new SaveCaptureOptions();
        if (loaded is not null) return new SaveBuilder(loaded.Source.Clone(), options.Seed);
        var builder = new SaveBuilder(options.Seed);
        AddFreshFactions(builder, data);
        AddFreshTowns(builder, data, options);
        builder.AddEmptyWorldState();
        return builder;
    }

    /// <summary><paramref name="prepared"/> is a <see cref="PrepareBase"/> made with the same <paramref name="loaded"/> (null: made here, which is the slow part).</summary>
    public static SaveGame Capture(World world, PopulationData data, SaveClock clock, LoadedSave? loaded = null, SaveCaptureOptions? options = null, SaveBuilder? prepared = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(data);
        options ??= new SaveCaptureOptions();
        // The item on the mouse is not saved: it goes back into its window first (docs/game/ui-screens.md section 6 "Holding an item").
        world.ReturnHeldItem();

        var builder = prepared ?? PrepareBase(data, loaded, options);
        var game = builder.Game;
        var camera = game.Camera;
        memoryPass = new MemoryPass();
        SavedKit.OwnerNotes = memoryPass.Owners;

        camera.Day = clock.Day;
        camera.Hour = clock.Hour;
        camera.Minute = clock.Minute;
        camera.PlayerMoney = (int)Math.Clamp(world.Player.Money, int.MinValue, int.MaxValue);
        camera.Version = SaveGame.GameVersion;
        camera.SetMods(options.DataFiles);
        if (data.PlayerFaction >= 0) camera.PlayerFactionName = world.Player.FactionName ?? data.Factions[data.PlayerFaction].Name;
        SquadAiKeys.Write(camera.Record, world.Player.SquadAi);
        camera.Record.Ints[SquadAiKeys.FormationKey] = SquadAiKeys.FormationOf(world, world.Player.Faction, data.Factions);
        camera.Record.Bools["bl"] = world.Player.BanditsLoot;
        BedSave.Write(camera.Record, world);
        camera.Record.Strings[SaveLoader.SeedKey] = world.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (options.View is { } view)
        {
            // FUN_1406b0740: the pivot's position and orientation, the camera's local z, alt and the tracked object; Meitou's camera follows nobody.
            (camera.Rotation, camera.Zoom) = KenshiCamera.ToSaved(view.Yaw, view.Pitch, view.Distance);
            camera.Position = view.Pivot;
            camera.Record.Floats["alt"] = 0;
            camera.Tracking = Hand.Null;
        }

        var factionsPopulation = world.Systems.OfType<PopulationSystem>().FirstOrDefault();
        UpdateFactions(game, data, loaded, world, factionsPopulation);
        WriteKnownFactions(game, data, world.Player);
        WriteTownKnowledge(builder, data, world.Player);
        if (factionsPopulation is { } pools)
        {
            WriteTownPools(game, data, pools);
            WriteTownArtifacts(game, data, world, pools);
        }
        WriteTownReplacements(game, data);
        // The zone files of the zones visited since (before the locks, the player's buildings and the home handles, which write into them).
        ZoneSave.WriteVisited(game, world, data, options.Levels);
        var buildings = HeldSave.BuildingStates(game);
        WriteLocks(world, buildings);
        world.Fixtures.WriteResets(buildings);
        ResearchSave.Write(game.Research, world.Research);
        var outposts = data.PlayerFaction >= 0 ? BuildingSave.Write(game, world, data.Factions[data.PlayerFaction].Id, options.Seed) : new Dictionary<int, Hand>();
        // Again, with the states of the buildings first written now (job targets name them by handle).
        buildings = HeldSave.BuildingStates(game);
        MachineSave.Write(game, world);
        world.Systems.OfType<Combat.CombatSystem>().FirstOrDefault()?.WriteTurretShots(world, buildings);
        GroundItemSave.Write(game, world, data.Items);
        var races = new Dictionary<string, RaceData?>(StringComparer.Ordinal);
        var written = WritePlayerSquads(builder, world, data, loaded, options, clock, buildings, races);
        WriteSelection(game, world, written);
        WriteUniques(game, world, written);
        // The NPC platoons: the save's as they are now, and the population's own squads in the world as new platoons (docs/simulation.md "Saves").
        if (loaded is not null) WriteRoaming(game, world, loaded);
        WriteSavedNpcs(builder, world, data, options, clock, buildings, races);
        var fresh = WriteFreshNpcs(builder, world, data, options, clock, buildings, races, loaded);
        // The war states name the campaign platoons by the handles they were just written with.
        if (world.Systems.OfType<PopulationSystem>().FirstOrDefault() is { } wars) WarSave.Write(game, world, data, wars, PlatoonHands(game, world, loaded, fresh), outposts);
        // The wild: the nests made so far in the town list the original's way, and the wild squads in the world as new platoons of their nest or sector.
        if (world.Systems.OfType<PopulationSystem>().FirstOrDefault() is { } population)
        {
            var nests = WriteNests(builder, world, data, population, options.Seed);
            WriteWildNpcs(builder, world, data, population, nests, options, clock, buildings, races);
        }

        WriteMemories();
        game.UpdateDerived();
        return game;
    }

    // ------------------------------------------------------------------ squad memory

    /// <summary>What a capture collects for the squads' memories: the handle every character written got, and the platoon records to put each squad's memory on once all are written.</summary>
    sealed class MemoryPass
    {
        public readonly Dictionary<CharacterId, Hand> Handles = [];
        public readonly List<(FcsRecord Record, SquadMemoryData? Data)> Targets = [];
        public readonly List<(FcsRecord State, CharacterId Partner)> Carries = [];
        public readonly List<(FcsRecord Record, string Prefix, CharacterId Target)> OrderTargets = [];
        public readonly List<(SaveItem Item, CharacterId Owner)> Owners = [];
    }

    [ThreadStatic] static MemoryPass? memoryPass;

    static Dialogue.SquadMemory? MemoryOf(World world) => world.Systems.OfType<Dialogue.DialogueSystem>().FirstOrDefault()?.Memory;

    /// <summary>Puts a squad's memory (null: none, which clears the record's) on a platoon record once the capture has written every character (<see cref="MemorySave"/>).</summary>
    static void NoteMemory(FcsRecord record, SquadMemoryData? data) => memoryPass?.Targets.Add((record, data));

    static void WriteMemories()
    {
        if (memoryPass is not { } pass) return;
        memoryPass = null;
        foreach (var (record, data) in pass.Targets)
            MemorySave.Write(record, data, id => pass.Handles.TryGetValue(id, out var hand) ? hand : null);
        // The carry links (FUN_1406280a0 writes the partner's handle under `carrying`, the null one when it is not saved).
        foreach (var (state, partner) in pass.Carries) (pass.Handles.TryGetValue(partner, out var hand) ? hand : Hand.Null).Write(state, "carrying");
        // The queued orders name their target character the same way (`tjobT<i>`); one not saved leaves the null handle.
        foreach (var (record, prefix, who) in pass.OrderTargets) (pass.Handles.TryGetValue(who, out var hand2) ? hand2 : Hand.Null).Write(record, prefix);
        // Stolen items name the robbed character (`ownedby`); one not saved leaves the null handle the item was written with.
        SavedKit.OwnerNotes = null;
        foreach (var (item, owner) in pass.Owners)
            if (item.Written is { } record && pass.Handles.TryGetValue(owner, out var hand)) hand.Write(record, "ownedby");
    }

    // ------------------------------------------------------------------ factions and towns

    static void AddFreshFactions(SaveBuilder builder, PopulationData data)
    {
        for (int a = 0; a < data.Factions.Count; a++)
        {
            var f = data.Factions[a];
            bool player = a == data.PlayerFaction;
            var table = new List<SaveRelation>();
            if (!player)
                for (int b = 0; b < data.Factions.Count; b++)
                    table.Add(new SaveRelation(data.Factions[b].Id, data.Relations.Get(a, b), 0, 0, data.Relations.AtWar(a, b)));
            builder.AddFaction(f.Id, f.Name, player, 1000, table, rank: data.Relations.Rank(a)).GlobalTrust = data.Relations.GlobalTrust(a);
        }
    }

    static void AddFreshTowns(SaveBuilder builder, PopulationData data, SaveCaptureOptions options)
    {
        if (options.Placements is null) return;
        foreach (var p in options.Placements)
        {
            if (data.Db.Find(p.TownId) is not { Type: FcsRecordType.TOWN } record) continue;
            var town = TownData.From(record);
            // The level state's handle, as the original keeps it (docs/formats/save.md "Object handles"): the buildings' and platoons' town handles name it.
            Hand? hand = p.StateId is not null && options.Levels?.Level.Find(p.StateId) is { } level && level.Ints.ContainsKey("handI")
                ? new Hand(level.GetInt("handTYPE"), level.GetInt("handC"), level.GetInt("handCS"), level.GetInt("handI"), level.GetInt("handS")) : null;
            builder.AddTown("Town state " + record.Name, p.InstanceId, town.Faction ?? "", isNest: false, WorldLayout.ZoneOf(p.Position.X, p.Position.Z), hand: hand);
        }
    }

    /// <summary>
    /// The instance of a camp's town state in Meitou saves written before nests were saved the original's way (wild site and TOWN record): still read, and still updated
    /// in a save that has one, never written new (camps are now their nest's state, <see cref="WriteNests"/>).
    /// </summary>
    internal const string CampInstancePrefix = "meitou-nest:";

    /// <summary>Writes what the player knows of each placed town into its state, by instance id. Flags are only added: the world never forgets a town.</summary>
    static void WriteTownKnowledge(SaveBuilder builder, PopulationData data, PlayerState player)
    {
        var game = builder.Game;
        var known = player.Towns;
        var byInstance = new Dictionary<string, TownKnowledge>(StringComparer.Ordinal);
        for (int i = 0; i < known.Length && i < data.Sites.Count; i++)
            if (known[i] != 0 && data.Sites[i].InstanceId.Length > 0) byInstance[data.Sites[i].InstanceId] = known[i];
        foreach (var camp in player.Camps)
            byInstance[CampInstancePrefix + camp.Site.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + camp.TownId] = camp.Known;
        foreach (var town in game.Towns)
        {
            if (!byInstance.TryGetValue(town.Instance, out var flags)) continue;
            if ((flags & TownKnowledge.Discovered) != 0) town.Record.Bools["discovered"] = true;
            if ((flags & TownKnowledge.Explored) != 0) town.Record.Bools["explored"] = true;
            town.Record.Bools["recently discovered"] = (flags & TownKnowledge.Recent) != 0;
        }
    }

    /// <summary>The towns' pools (<c>pop</c>, <c>popd</c>, <c>pop2</c>, <c>popd2</c>, <c>started</c>, <c>ded</c>, <c>tod</c>) into their states, for each town made or loaded with pools.</summary>
    static void WriteTownPools(SaveGame game, PopulationData data, PopulationSystem population)
    {
        var sites = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in data.Sites) if (s.InstanceId.Length > 0) sites.TryAdd(s.InstanceId, s.Index);
        foreach (var town in game.Towns)
            if (!town.IsNest && sites.TryGetValue(town.Instance, out int site) && population.PoolsOf(site) is { } pools) {
                pools.Write(town.Record);
                town.Record.Bools["started"] = pools.Started;
            }
    }

    /// <summary>Each placed town's artifacts still to be given out into its state (docs/game/economy.md "Artifacts").</summary>
    static void WriteTownArtifacts(SaveGame game, PopulationData data, World world, PopulationSystem population)
    {
        var sites = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in data.Sites) if (s.InstanceId.Length > 0) sites.TryAdd(s.InstanceId, s.Index);
        foreach (var town in game.Towns)
            if (!town.IsNest && sites.TryGetValue(town.Instance, out int site)) PopulationSystem.WriteArtifacts(town.Record, population.ArtifactsOf(world, site));
    }

    /// <summary>Each placed town's <c>replacementTown</c> (the TOWN in its place, "" none) and, while replaced, its <c>faction</c>, into its state.</summary>
    static void WriteTownReplacements(SaveGame game, PopulationData data)
    {
        var sites = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in data.Sites) if (s.InstanceId.Length > 0) sites.TryAdd(s.InstanceId, s.Index);
        foreach (var town in game.Towns)
        {
            if (town.IsNest || !sites.TryGetValue(town.Instance, out int site)) continue;
            var replacement = data.Sites[site].Replacement;
            if (replacement is null && !town.Record.Strings.ContainsKey("replacementTown")) continue;
            town.Record.Strings["replacementTown"] = replacement?.Id ?? "";
            if (replacement is not null && data.Sites[site].Town.Faction is { } faction) town.Record.Strings["faction"] = faction;
        }
    }

    static void UpdateFactions(SaveGame game, PopulationData data, LoadedSave? loaded, World world, PopulationSystem? population)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < data.Factions.Count; i++) index[data.Factions[i].Id] = i;
        foreach (var f in game.Factions)
        {
            if (loaded?.FactionOf(f.Id) is { } state)
            {
                f.Prosperity = state.Prosperity;
                f.PlatoonCounter = Math.Max(f.PlatoonCounter, state.PlatoonCounter);
            }
            // The population's running prosperity (it started at the loaded value or the cap).
            if (population is not null && index.TryGetValue(f.Id, out int own)) f.Prosperity = (float)population.ProsperityOf(world, own);
            if (loaded is null || !index.TryGetValue(f.Id, out int a) || loaded.RelationBaseline.Length != index.Count * index.Count) continue;
            if (a < loaded.TrustBaseline.Length && data.Relations.GlobalTrust(a) != loaded.TrustBaseline[a]) f.GlobalTrust = data.Relations.GlobalTrust(a);
            if (f.IsPlayer) continue;
            if (a < loaded.RankBaseline.Length && data.Relations.Rank(a) != loaded.RankBaseline[a]) f.Rank = data.Relations.Rank(a);
            var table = f.Relations;
            var updated = new List<SaveRelation>(table.Count);
            bool changed = false;
            foreach (var r in table)
            {
                // A value the world changed since loading is written; one it did not touch stays as the save had it.
                float value = r.Relation;
                bool war = r.War;
                if (index.TryGetValue(r.FactionId, out int b))
                {
                    if (data.Relations.Get(a, b) != loaded.RelationBaseline[a * index.Count + b]) value = data.Relations.Get(a, b);
                    if (loaded.WarBaseline.Length == loaded.RelationBaseline.Length && data.Relations.AtWar(a, b) != loaded.WarBaseline[a * index.Count + b]) war = data.Relations.AtWar(a, b);
                }
                changed |= value != r.Relation || war != r.War;
                updated.Add(r with { Relation = value, War = war });
            }
            if (changed) f.SetRelations(updated);
        }
    }

    /// <summary>
    /// The player faction's <c>known</c> list (on the record a load reads it from, <see cref="SaveGame.PlayerFaction"/>): the factions met, in faction order, after the ids the game
    /// data does not have (kept, as their faction states are).
    /// </summary>
    static void WriteKnownFactions(SaveGame game, PopulationData data, PlayerState player)
    {
        if (game.PlayerFaction is not { } mine) return;
        mine.Known = [.. mine.Known.Where(id => data.FactionIndex(id) < 0), .. player.KnownFactions.Order().Select(f => data.Factions[f].Id)];
    }

    // ------------------------------------------------------------------ the player's platoons

    static Dictionary<CharacterId, SaveCharacter> WritePlayerSquads(SaveBuilder builder, World world, PopulationData data, LoadedSave? loaded, SaveCaptureOptions options, SaveClock clock,
        IReadOnlyDictionary<string, FcsRecord> buildings, Dictionary<string, RaceData?> races)
    {
        var game = builder.Game;
        var table = world.Characters;
        var written = new Dictionary<CharacterId, SaveCharacter>();
        string playerFactionId = data.PlayerFaction >= 0 ? data.Factions[data.PlayerFaction].Id : "204-gamedata.base";
        string playerFactionName = world.Player.FactionName ?? (data.PlayerFaction >= 0 ? data.Factions[data.PlayerFaction].Name : "Nameless");
        var playerState = game.Factions.FirstOrDefault(f => f.Id == playerFactionId);
        int squads = 0, members = 0;

        // Platoons of the loaded save whose characters the world held: those not alive any more are removed.
        var spawned = loaded?.SpawnedSlots ?? [];
        var seen = new HashSet<(string Platoon, int Slot)>();

        // The player's squads in order (their `squad index`), then any other squad holding characters of a loaded platoon (a prisoner or slave kept in it).
        var order = SquadManagement.PlayerSquads(world);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var squad in order.Concat(world.Squads.All.Where(s => s.PlatoonId < 0 && !order.Contains(s))))
        {
            // A member of a loaded platoon is written whoever owns it (a prisoner or slave kept in the squad is not the player's but is in the file); only the
            // resident squads the population system made, which have no link, are left out by the player test.
            var alive = squad.Members.Where(m => table.IsAlive(m) && (table.Cold(m.Slot)!.IsPlayer || table.Cold(m.Slot)!.Save is not null)).ToList();
            int index = order.IndexOf(squad);
            // A player's squad is written even when empty: the original keeps empty squads (the squads screen removes them).
            if (alive.Count == 0 && index < 0) continue;
            squads++;

            // The squad's own platoon, by the file it was loaded from (never by its members: a character moved between squads would carry the platoon with it).
            string? name = squad.SaveName ?? (index < 0 && !squad.Dead ? alive.Select(m => table.Cold(m.Slot)!.Save?.PlatoonName).FirstOrDefault(n => n is not null) : null);
            var platoon = name is null || kept.Contains(name) ? null : game.Platoons.FirstOrDefault(p => p.Name == name);
            var at = alive.Count > 0 ? table.Previous[alive[0].Slot].Position : squad.Position;
            if (platoon is null)
                platoon = builder.AddPlatoon(FreshName(game, playerState, playerFactionName), playerFactionName, playerFactionId, squad.TemplateId, at);
            else
                platoon.Position = at;
            kept.Add(platoon.Name);
            NoteMemory(platoon.Record, MemoryOf(world)?.Snapshot(squad.Id));
            if (index >= 0)
            {
                // The name the tabs show is the record's name; the string id stays the file's (Observed: rename writes the record's name, docs/formats/save.md).
                if (squad.Name.Length > 0) platoon.Record.Name = squad.Name;
                platoon.Record.Ints["squad index"] = index;
            }
            else if (squad.Dead) platoon.Record.Name = SquadManagement.DeadSquadName;

            foreach (var id in alive)
            {
                members++;
                var cold = table.Cold(id.Slot)!;
                SaveCharacter? sc = null;
                if (cold.Save is { } link && link.PlatoonName == platoon.Name)
                {
                    sc = platoon.Characters.FirstOrDefault(c => c.Handle.I == link.Slot);
                    seen.Add((link.PlatoonName, link.Slot));
                }
                written[id] = WriteCharacter(builder, world, data, options, clock, buildings, races, platoon, sc, id, playerFactionId, squad.Leader == id, orders: true);
            }
            // The platoon's `imprisoned` is set only when it unloads (FUN_1407ef4b0), which these squads do not: the record keeps the save's value.
            SetCharCount(platoon);
        }

        // Characters of a loaded platoon that the world had and now has no more (killed, left the squad): gone from the file.
        foreach (var (platoonName, slot) in spawned)
        {
            if (seen.Contains((platoonName, slot))) continue;
            var platoon = game.Platoons.FirstOrDefault(p => p.Name == platoonName);
            var sc = platoon?.Characters.FirstOrDefault(c => c.Handle.I == slot);
            if (platoon is not null && sc is not null) builder.RemoveCharacter(platoon, sc);
        }
        // A loaded player squad removed since (DELETE on the squads screen): its platoon goes, as the original destroys it (FUN_1406ba9d0).
        foreach (var lp in loaded?.PlayerPlatoons ?? [])
            if (!kept.Contains(lp.Platoon.Name) && world.Squads.Find(lp.Squad.Id) is null && game.Platoons.FirstOrDefault(p => p.Name == lp.Platoon.Name) is { } gone)
                builder.RemovePlatoon(gone);

        game.Camera.Squads = squads;
        game.Camera.Members = members;
        return written;
    }

    /// <summary>The next free <c>&lt;faction name&gt;_&lt;n&gt;</c> platoon name of a faction, counting its platoon counter on.</summary>
    static string FreshName(SaveGame game, SaveFaction? faction, string factionName)
    {
        int n = faction?.PlatoonCounter ?? 0;
        while (game.Platoons.Any(p => p.Name == $"{factionName}_{n}")) n++;
        if (faction is not null) faction.PlatoonCounter = n + 1;
        return $"{factionName}_{n}";
    }

    static void SetCharCount(SavePlatoon platoon)
    {
        platoon.CharCount = platoon.Characters.Count;
        if (platoon.Collection is { } c) c.Ints["char count"] = platoon.Characters.Count;
        platoon.Record.Ints["char count"] = platoon.Characters.Count;
    }

    /// <summary>
    /// One character as it is in the world into its platoon file: its records (<paramref name="sc"/>), or new ones when null. Place and facing, name, leader flag,
    /// bounties, crime marks, the player's orders (<paramref name="orders"/>, the player's squads), bed or cage; stats and medical state (from the host, else the
    /// loaded ones, else defaults for a new one); a new one's look; what it carries.
    /// </summary>
    static SaveCharacter WriteCharacter(SaveBuilder builder, World world, PopulationData data, SaveCaptureOptions options, SaveClock clock, IReadOnlyDictionary<string, FcsRecord> buildings,
        Dictionary<string, RaceData?> races, SavePlatoon platoon, SaveCharacter? sc, CharacterId id, string ownerFactionId, bool isLeader, bool orders)
    {
        ref readonly var hot = ref world.Characters.Previous[id.Slot];
        return WriteCharacter(builder, world, data, options, clock, buildings, races, platoon, sc, world.Characters.Cold(id.Slot)!, hot.Position, hot.Yaw, id, ownerFactionId, isLeader, orders);
    }

    /// <summary>The same for a character as <paramref name="cold"/> standing at <paramref name="position"/> facing <paramref name="yaw"/>: one in the world (<paramref name="id"/>) or a stored one (none).</summary>
    static SaveCharacter WriteCharacter(SaveBuilder builder, World world, PopulationData data, SaveCaptureOptions options, SaveClock clock, IReadOnlyDictionary<string, FcsRecord> buildings,
        Dictionary<string, RaceData?> races, SavePlatoon platoon, SaveCharacter? sc, CharacterCold cold, Vector3 position, float yaw, CharacterId id, string ownerFactionId, bool isLeader, bool orders)
    {
        var hot = (Position: position, Yaw: yaw);
        bool isNew = sc is null;
        sc ??= builder.AddCharacter(platoon, cold.RecordId, cold.Name, ownerFactionId, hot.Position, SaveRotation.OfYaw(hot.Yaw), isLeader: isLeader, squadMemberType: cold.Role);
        // The handle's own serial is "random" in the original, and the loader rolls a character's look from it: a character with a rolled look gets the look's seed
        // as its serial, so loading a save without its items (one written before inventories were) rolls the same face, hair and clothes again.
        if (isNew && cold.Appearance?.Loadout is { } loadout) sc.Handle = sc.Handle with { S = loadout.Seed };

        // Where and which way.
        sc.Position = hot.Position;
        var link = cold.Save;
        sc.Rotation = link is not null && MathF.Abs(SaveRotation.Difference(hot.Yaw, link.Yaw)) < 1e-4f ? link.Rotation : SaveRotation.OfYaw(hot.Yaw);
        if (sc.GameState is { } g)
        {
            g.Strings["name"] = cold.Name;
            g.Bools["is leader"] = isLeader;
            BountySave.Write(g, cold.Bounties, data.Relations, clock, world.Tick, world.TickSeconds);
            CrimeSave.Write(g, cold);
            SlaveSave.Write(g, cold, data);
            DialogueSave.Write(g, cold);
            if (orders) Orders.Write(g, cold);
            if (orders && sc.Ai is { } ai) PermaJobSave.Write(ai, cold, world.Buildings, buildings, who => world.Characters.TryResolveNext(who, out int target) && world.Characters.Cold(target)?.Save is { } link2 ? link2.Source.Handle : Hand.Null);
            if (orders && sc.Ai is { } queueAi)
                OrderSave.Write(queueAi, cold, world, buildings, (record, prefix, who) =>
                {
                    if (memoryPass is { } pass) pass.OrderTargets.Add((record, prefix, who));   // the handle is written once every character has one
                    else if (world.Characters.TryResolveNext(who, out int slot) && world.Characters.Cold(slot)?.Save is { } held) held.Source.Handle.Write(record, prefix);
                });
            HeldSave.Write(g, cold, link, world.Fixtures, buildings);
            // Carrying or carried: the bool says which, the partner's handle is written once every character has one.
            g.Bools["carrying"] = !cold.Carrying.IsNone;
            var partner = cold.Carrying.IsNone ? cold.CarriedBy : cold.Carrying;
            if (partner.IsNone) Hand.Null.Write(g, "carrying"); else memoryPass?.Carries.Add((g, partner));
            // A world without ragdolls (headless) keeps what was read; a stale pose is not used where the character no longer lies.
            if (id.IsNone) RagdollSave.Write(g, null);
            else if (world.Systems.OfType<Ragdolls.RagdollSystem>().FirstOrDefault() is { } ragdolls) RagdollSave.Write(g, ragdolls.SaveState(id));
            if (cold.PortraitSerial is { } portraitSerial) g.Ints["portrait_serial"] = (int)portraitSerial;
            // The character's slot 0x398 (FUN_1406280a0): an animal's age, 1 for people (docs/game/animals.md "Ageing"; Verified in vanilla saves).
            if ((cold.Stats ?? link?.Stats) is { } aged) g.Floats["age"] = aged.Scale;
        }

        // Body: stats and medical state, from the host, else the loaded ones, else (for a new character) defaults.
        var (stats, medical) = (id.IsNone ? null : options.Bodies?.Invoke(id)) ?? (null, null);
        stats ??= cold.Stats ?? link?.Stats;
        medical ??= cold.Medical ?? link?.Medical;
        WriteBody(sc, cold.RecordId, stats, medical, isNew, data, races);
        if (isNew || cold.LookEdited) WriteAppearance(sc, cold, data.Db, races, replace: !isNew);
        // What it carries and wears, as it is now; items read from a save keep their records' other keys.
        if (cold.Inventory is { } inventory) builder.SetInventory(platoon, sc, SavedKit.FromInventory(inventory));
        if (!id.IsNone) memoryPass?.Handles[id] = sc.Handle;
        return sc;
    }

    static void WriteBody(SaveCharacter sc, string recordId, CharacterStats? stats, MedicalState? medical, bool isNew, PopulationData data, Dictionary<string, RaceData?> races)
    {
        if (stats is null && isNew) stats = new CharacterStats();
        if (stats is not null && sc.Stats is { } sr)
        {
            stats.WriteSave(sr.Floats);
            SaveBuilder.FillLegacyStats(sr);
        }
        if (medical is null && isNew && RaceOf(recordId, data.Db, races) is { } race)
            medical = MedicalState.Create(race, stats?.Strength ?? 50);
        if (medical is not null && sc.Medical is { } mr)
        {
            medical.WriteSave(mr.Floats, mr.Bools, mr.Strings);
            if (sc.Platoon.File is { } file) SavedLimbs.Write(medical, file, mr);
        }
    }

    // ------------------------------------------------------------------ NPC platoons

    /// <summary>
    /// The save's NPC platoons (docs/simulation.md "Saves"): a stand-in that travelled gets its position and package; one whose characters were in the world gets
    /// them as they are (in the world now) or were (when it left): place, facing, body, kit, bounties; a character whose body is gone leaves the file.
    /// </summary>
    static void WriteSavedNpcs(SaveBuilder builder, World world, PopulationData data, SaveCaptureOptions options, SaveClock clock, IReadOnlyDictionary<string, FcsRecord> buildings,
        Dictionary<string, RaceData?> races)
    {
        if (world.Saved is not { } saved) return;
        var game = builder.Game;
        var table = world.Characters;
        var byName = new Dictionary<string, SavePlatoon>(StringComparer.Ordinal);
        foreach (var x in game.Platoons) byName.TryAdd(x.Name, x);
        foreach (var sp in saved.Platoons)
        {
            if (!byName.TryGetValue(sp.Name, out var platoon)) continue;
            // Despawned off screen (the Faithful switch, or wiped out in an off-screen fight): the original deletes such a platoon's record.
            if (sp.Despawned)
            {
                builder.RemovePlatoon(platoon);
                continue;
            }
            if (!sp.Touched) continue;   // a stand-in that travelled is written by WriteRoaming, a never-made one once made by WriteFreshNpcs
            var p = sp.PlatoonId >= 0 ? world.Platoons.Find(sp.PlatoonId) : null;
            var squad = p is { State: PlatoonState.Loaded } ? world.Squads.Find(p.SquadId) : null;
            if (squad is not null && table.TryResolveNext(squad.Leader, out int leaderSlot)) platoon.Position = table.Previous[leaderSlot].Position;
            if (p is not null) platoon.Money = (int)Math.Clamp(squad?.Money ?? p.Money, int.MinValue, int.MaxValue);
            // `imprisoned` as it was at the platoon's last unload, else the save's value (FUN_1407ef4b0).
            if (p is not null && (p.Imprisoned || platoon.Record.Bools.ContainsKey("imprisoned"))) platoon.Record.Bools["imprisoned"] = p.Imprisoned;
            if (p is not null && (p.Dead || platoon.Record.Bools.ContainsKey("dead"))) platoon.Record.Bools["dead"] = p.Dead;   // +0x1f0
            if (p is not null) platoon.Record.Bools["intact"] = p.IntactFlag;   // +0x100
            ShopSave.Write(platoon.Record, squad is not null ? squad.Shop : sp.Shop, world);
            AiSave.Write(platoon.Record, squad is not null ? AiSave.Of(squad.Ai) ?? squad.SavedAi : sp.Ai);
            // A wild one the AI ran: its package, persistent while it occupies a town (as the original's occupation).
            if (sp.Wild is not null && p is not null && (squad?.Ai?.Current?.Id ?? p.Package) is { } package)
            {
                platoon.Record.Strings["currentPackage"] = package;
                platoon.Record.Bools["persistent"] = Ai.AiSystem.Occupies(data.Ai, package);
            }
            if (squad is not null) NoteMemory(platoon.Record, MemoryOf(world)?.Snapshot(squad.Id));
            else if (sp.MemorySet) NoteMemory(platoon.Record, sp.Memory);
            foreach (var m in sp.Members)
            {
                var sc = platoon.Characters.FirstOrDefault(c => c.Handle.I == m.Slot);
                if (sc is null) continue;
                // A corpse that decayed or was unloaded since it was made leaves its platoon's list, as the original's destroyed character does.
                if (m.Gone || !m.Live.IsNone && !table.IsAlive(m.Live) && sc.Medical?.Bools.GetValueOrDefault("dead") == true)
                {
                    builder.RemoveCharacter(platoon, sc);
                    continue;
                }
                if (!m.Live.IsNone && table.IsAlive(m.Live))
                    WriteCharacter(builder, world, data, options, clock, buildings, races, platoon, sc, m.Live, sc.OwnerFactionId, squad?.Leader == m.Live, orders: false);
                else if (m.Position is { } at)
                {
                    // As it was when its platoon left the world.
                    sc.Position = at;
                    if (MathF.Abs(SaveRotation.Difference(m.Yaw, SaveRotation.YawOf(sc.Rotation))) >= 1e-4f) sc.Rotation = SaveRotation.OfYaw(m.Yaw);
                    WriteBody(sc, sc.RecordId, m.Stats, m.Medical, false, data, races);
                    if (sc.GameState is { } g)
                    {
                        BountySave.Write(g, m.Bounties, data.Relations, clock, world.Tick, world.TickSeconds);
                        RagdollSave.Write(g, null);   // its ragdoll went with it
                    }
                    if (m.Inventory is { } inventory) builder.SetInventory(platoon, sc, SavedKit.FromInventory(inventory));
                }
            }
            SetCharCount(platoon);
        }
    }

    /// <summary>
    /// The PLATOON handle of every world platoon the save holds: the loaded save's (stand-ins and saved platoons, by name; not those despawned) and the ones just written
    /// (<paramref name="fresh"/>).
    /// </summary>
    static Dictionary<int, Hand> PlatoonHands(SaveGame game, World world, LoadedSave? loaded, Dictionary<int, SavePlatoon> fresh)
    {
        var byName = new Dictionary<string, SavePlatoon>(StringComparer.Ordinal);
        foreach (var x in game.Platoons) byName.TryAdd(x.Name, x);
        var hands = new Dictionary<int, Hand>();
        if (loaded is not null)
            foreach (var (id, name) in loaded.RoamingPlatoons)
                if (byName.TryGetValue(name, out var sp)) hands[id] = sp.Handle;
        if (world.Saved is { } saved)
            foreach (var sp in saved.Platoons)
                if (sp.PlatoonId >= 0 && !sp.Despawned && byName.TryGetValue(sp.Name, out var written)) hands[sp.PlatoonId] = written.Handle;
        foreach (var (id, sp) in fresh) hands[id] = sp.Handle;
        return hands;
    }

    /// <summary>
    /// The NPC squads the population made (a town's residents and bar squads, in the world or stored since their town unloaded, and roaming squads while they are in
    /// the world) as new platoons, so a save keeps them where they are: a new PLATOON with its <c>basetown</c>, <c>is resident</c> for a town's residents and the AI
    /// package running; its characters as new ones (look, kit, body). Wild squads and roaming stand-ins (no characters) are not written: the population makes them again;
    /// but a campaign's stand-in is (its war state names it), as a PLATOON with no file, which the loader makes from its template. Returns the platoons written by world platoon id.
    /// </summary>
    static Dictionary<int, SavePlatoon> WriteFreshNpcs(SaveBuilder builder, World world, PopulationData data, SaveCaptureOptions options, SaveClock clock, IReadOnlyDictionary<string, FcsRecord> buildings,
        Dictionary<string, RaceData?> races, LoadedSave? loaded)
    {
        var written = new Dictionary<int, SavePlatoon>();
        var game = builder.Game;
        var table = world.Characters;
        var factions = new Dictionary<string, SaveFaction>(StringComparer.Ordinal);
        foreach (var f in game.Factions) factions.TryAdd(f.Id, f);
        var townHands = new Dictionary<string, Hand>(StringComparer.Ordinal);
        foreach (var t in game.Towns)
            if (t.Instance.Length > 0 && !t.Handle.IsNull) townHands.TryAdd(t.Instance, t.Handle);
        var byName = new Dictionary<string, SavePlatoon>(StringComparer.Ordinal);
        foreach (var x in game.Platoons) byName.TryAdd(x.Name, x);

        // A new record, or (`into`) a save's never-made platoon Meitou made from its template (Platoon.Unmade): its own record written back, as the original's saver
        // does (FUN_140372ef0: `never been activated` 0, its file and char count); its pool, residency, town and home stay as the record has them.
        SavePlatoon Write(int town, int factionIndex, string templateId, long money, bool roaming, TownBuilding? homeBuilding, string? package,
            List<(CharacterCold Cold, Vector3 Position, float Yaw, CharacterId Id, bool Leader)> members, Trading.Shop? shop, Vector3 at = default, AiSave? ai = null, SquadMemoryData? memory = null,
            bool imprisoned = false, bool dead = false, bool intact = true, SavePlatoon? into = null)
        {
            var faction = data.Factions[factionIndex];
            factions.TryGetValue(faction.Id, out var state);
            var site = data.Sites[town];
            var lead = members.FirstOrDefault(m => m.Leader);
            if (lead.Cold is null && members.Count > 0) lead = members[0];
            if (members.Count == 0) lead.Position = at;
            int purse = (int)Math.Clamp(money, int.MinValue, int.MaxValue);
            SavePlatoon platoon;
            if (into is not null)
            {
                platoon = into;
                builder.GiveFile(platoon);
                platoon.Position = lead.Position;
                platoon.Money = purse;
                platoon.Record.Bools["never been activated"] = false;
            }
            else
            {
                platoon = builder.AddPlatoon(FreshName(game, state, faction.Name), faction.Name, faction.Id, templateId, lead.Position, purse);
                if (members.Count == 0)
                {
                    // A stand-in with no characters: no platoon file, as the original writes a platoon that is not loaded (`content file` "platoon/").
                    platoon.File = null;
                    platoon.ContentFile = "platoon/";
                }
            }
            var record = platoon.Record;
            if (into is null) record.Strings["basetown"] = site.Town.Id;
            record.Bools["imprisoned"] = imprisoned;
            record.Bools["dead"] = dead;
            record.Bools["intact"] = intact;
            if (into is null)
            {
                int siteFaction = data.SiteFactions.Count > town ? data.SiteFactions[town] : -1;
                // `sqt` is the town pool it counts against: 1 residents (and bar squads), 2 roaming (docs/formats/save.md "NPC platoons").
                record.Ints["sqt"] = roaming ? 2 : 1;
                // Residents and bar squads are persistent (the original keeps them off screen, docs/game/factions-squads-towns.md "Persistent platoons"), roaming ones not but
                // while they occupy a town.
                record.Bools["persistent"] = !roaming || Ai.AiSystem.Occupies(data.Ai, package);
                // A squad with a home is a resident (every platoon of the samples with a file and a `homebuilding` has `is resident`, and only those): a default resident too.
                record.Bools["is resident"] = !roaming && (homeBuilding is not null
                    || PopulationSystem.ResidentEntries(site.Town, siteFaction >= 0 ? data.Factions[siteFaction] : null).Any(e => e.Id == templateId));
            }
            if (package is not null) record.Strings["currentPackage"] = package;
            ShopSave.Write(record, shop, world);
            AiSave.Write(record, ai);
            NoteMemory(record, memory);
            if (into is null)
            {
                // Its town and home building by handle, as the original names them (docs/formats/save.md "NPC platoons").
                if (townHands.TryGetValue(site.InstanceId, out var townHand)) townHand.Write(record, "hometown");
                if (homeBuilding is { } home && HomeHandle(game, home) is { } homeHand) homeHand.Write(record, "homebuilding");
            }
            WriteMapArea(record, data, lead.Position);
            foreach (var m in members)
            {
                int f = m.Cold.Faction;
                string owner = f >= 0 && f < data.Factions.Count ? data.Factions[f].Id : faction.Id;
                WriteCharacter(builder, world, data, options, clock, buildings, races, platoon, null, m.Cold, m.Position, m.Yaw, m.Id, owner, ReferenceEquals(m.Cold, lead.Cold), orders: false);
            }
            SetCharCount(platoon);
            return platoon;
        }

        // A save's never-made platoon once made holds its squad in Stored beside its record (Platoon.Unmade).
        SavePlatoon? Into(Platoon? p) => p is { Saved: { } saved, Stored: not null } ? byName.GetValueOrDefault(saved.Name) : null;

        bool Writable(int town, int faction) => town >= 0 && town < data.Sites.Count && faction >= 0 && faction < data.Factions.Count && faction != data.PlayerFaction;

        foreach (var squad in world.Squads.All)
        {
            if (!Writable(squad.Town, squad.Faction)) continue;
            Platoon? roamer = null;
            if (squad.PlatoonId >= 0)
            {
                roamer = world.Platoons.Find(squad.PlatoonId);
                if (roamer is null || roamer.Saved is not null && roamer.Stored is null) continue;   // a save's platoon (WriteSavedNpcs)
            }
            // The dead stay in the platoon's list as the original writes it (FUN_1404ff410, docs/game/character-stats.md "Corpses").
            var every = squad.Members.Where(m => table.IsAlive(m) && table.Cold(m.Slot) is { IsPlayer: false, Save: null }).ToList();
            var alive = every.Where(m => table.Cold(m.Slot)!.DiedTick < 0).ToList();
            var into = Into(roamer);
            if (alive.Count == 0)
            {
                // A save's never-made platoon once made, wiped out, keeps its record: the original's saver (FUN_140372ef0) drops a declared-dead platoon unless
                // a member is carried or a player's squad is within 500, else writes it with its dead and `never been activated` 0 (a regenerating one too).
                if (into is null) continue;
                if (roamer!.Dead && !every.Any(m => !table.Cold(m.Slot)!.CarriedBy.IsNone) && !PlayerNear(world, roamer.Position))
                {
                    builder.RemovePlatoon(into);
                    continue;
                }
            }
            var leader = alive.Contains(squad.Leader) || alive.Count == 0 ? squad.Leader : alive[0];
            var members = every.Select(id => (table.Cold(id.Slot)!, table.Previous[id.Slot].Position, table.Previous[id.Slot].Yaw, id, id == leader)).ToList();
            var sp = Write(squad.Town, squad.Faction, squad.TemplateId, squad.Money, roamer is { Pinned: false }, squad.HomeBuilding, roamer?.Package ?? squad.Ai?.Current?.Id, members, squad.Shop, ai: AiSave.Of(squad.Ai) ?? squad.SavedAi, memory: MemoryOf(world)?.Snapshot(squad.Id),
                imprisoned: roamer?.Imprisoned ?? false, dead: roamer?.Dead ?? false, intact: roamer?.IntactFlag ?? squad.Ai?.Intact ?? true, at: roamer is null ? default : Ground(world, roamer.Position), into: into);
            if (roamer is not null) written[roamer.Id] = sp;
        }
        // Squads stored since they left the world: a town's since its town unloaded, a roaming one (Meitou) since it first left.
        foreach (var p in world.Platoons.All)
        {
            if (p is not { State: PlatoonState.Unloaded, Stored: { } stored } || !Writable(p.Origin, p.Faction)) continue;
            // A made never-made one with no one left (the dead are not stored) keeps its record too, but declared dead: the original drops an unloaded dead platoon.
            var into = Into(p);
            if (stored.Members.Count == 0 && (into is null || p.Dead))
            {
                if (into is not null) builder.RemovePlatoon(into);
                continue;
            }
            var members = stored.Members.Select(m => (m.Cold, m.Position, m.Yaw, CharacterId.None, m.Leader)).ToList();
            written[p.Id] = Write(p.Origin, p.Faction, p.TemplateId, p.Money, !stored.Pinned, stored.HomeBuilding, p.Package ?? stored.Package, members, stored.Shop, Ground(world, p.Position),
                ai: stored.Ai, memory: stored.Memory, imprisoned: p.Imprisoned, dead: p.Dead, intact: p.IntactFlag, into: into);
        }
        // A campaign's stand-ins that never had their characters (not a save's: those are in it already).
        foreach (var p in world.Platoons.All)
        {
            if (p is not { Campaign: not null, State: PlatoonState.Unloaded, Saved: null } || p.Stored is { Members.Count: > 0 } || !Writable(p.Origin, p.Faction)) continue;
            if (loaded?.RoamingPlatoons.ContainsKey(p.Id) == true) continue;
            written[p.Id] = Write(p.Origin, p.Faction, p.TemplateId, p.Money, true, null, p.Package, [], null, Ground(world, p.Position), imprisoned: p.Imprisoned, dead: p.Dead, intact: p.IntactFlag);
        }
        return written;
    }

    /// <summary>A stand-in's place (X, Z) on the ground.</summary>
    static Vector3 Ground(World world, Vector2 at) => new(at.X, world.Walkability.GroundHeight(at.X, at.Y), at.Y);

    /// <summary>Whether a squad of the player's is within 500 of a place (X, Z): the original's saver keeps a dead platoon then (<c>FUN_140372ef0</c>, 250000 squared).</summary>
    static bool PlayerNear(World world, Vector2 at)
    {
        var table = world.Characters;
        foreach (var s in world.Squads.All)
            if (s.Faction == world.Player.Faction && !s.Dead && table.TryResolveNext(s.Leader, out int slot)
                && Vector2.DistanceSquared(new Vector2(table.Previous[slot].Position.X, table.Previous[slot].Position.Z), at) < 500f * 500f)
                return true;
        return false;
    }

    /// <summary>
    /// The handle a platoon's <c>homebuilding</c> names a town building by: its state's <c>handle</c> when the save's zone file holds the building, else the handle of its
    /// level-data state, which the original gives the building when it loads a zone that has no saved state of it (docs/formats/save.md "Object handles"). Null when it has neither.
    /// </summary>
    static Hand? HomeHandle(SaveGame game, TownBuilding b)
    {
        if (game.Zones.TryGetValue(b.Zone, out var zone) && zone.BuildingList is { } list)
        {
            var entry = list.Instances.FirstOrDefault(i => i.Id == b.InstanceId);
            if (entry is not null && zone.File.Records.FirstOrDefault(r => r.RecordType == FcsRecordType.GAMESTATE_BUILDING && entry.States.Contains(r.StringId)) is { } state
                && Hand.Read(state, "handle") is { IsNull: false } saved)
                return saved;
        }
        return b.HandleSlot >= 0 ? new Hand((int)FcsRecordType.BUILDING, SaveZone.ContainerIdOf(b.Zone), ZoneContainerSerial, b.HandleSlot, b.HandleSerial) : null;
    }

    /// <summary>The <c>CS</c> of every zone container (docs/formats/save.md "Object handles").</summary>
    const int ZoneContainerSerial = 11111;

    /// <summary>The registered buildings' locks and doors, and the homes a lock-up round locked, into their zones' existing state records (a building the save has no record of, or a new save without zones, is left out).</summary>
    static void WriteLocks(World world, IReadOnlyDictionary<string, FcsRecord> buildings)
    {
        var locks = world.Systems.OfType<Locks.LockSystem>().FirstOrDefault();
        foreach (var b in world.Fixtures.Buildings)
        {
            if (!buildings.TryGetValue(b, out var state)) continue;
            world.Fixtures.WriteBuilding(b, state);
            if (locks is null) continue;
            if (locks.IsHouseLocked(world, b)) state.Bools[Locks.LockSystem.HouseLockedKey] = true;
            else state.Bools.Remove(Locks.LockSystem.HouseLockedKey);
        }
    }

    static RaceData? RaceOf(string recordId, GameDatabase db, Dictionary<string, RaceData?> cache)
    {
        if (db.Find(recordId) is not { } record || CharacterAssembly.RaceId(record) is not { } id) return null;
        return CharacterAssembly.RaceDataOf(db, id, cache);
    }

    /// <summary>
    /// The CHARACTER_APPEARANCE of a new character: the generated one when the character has a look (its sliders, head, hair, skin tone), else only the race and the
    /// editor flags. With <paramref name="replace"/> (a loaded character edited in the character editor) the record's keys become the look's exactly: the editor starts
    /// from a copy of the saved record, so the original's key set and order are kept. The loader reads the record back into the look (<see cref="SavedKit"/>).
    /// </summary>
    static void WriteAppearance(SaveCharacter sc, CharacterCold cold, GameDatabase db, Dictionary<string, RaceData?> races, bool replace = false)
    {
        var target = sc.Appearance;
        if (target is null) return;
        if (cold.Appearance?.Loadout?.Appearance.Record is { } source)
        {
            if (ReferenceEquals(source, target)) return;
            if (replace)
            {
                target.Bools.Clear(); target.Floats.Clear(); target.Ints.Clear(); target.Vector3s.Clear(); target.Strings.Clear(); target.References.Clear();
            }
            foreach (var (k, v) in source.Bools) target.Bools[k] = v;
            foreach (var (k, v) in source.Floats) target.Floats[k] = v;
            foreach (var (k, v) in source.Ints) target.Ints[k] = v;
            foreach (var (k, v) in source.Vector3s) target.Vector3s[k] = v;
            foreach (var (k, v) in source.Strings) target.Strings[k] = v;
            foreach (var (k, v) in source.References) target.References[k] = [.. v];
            return;
        }
        target.Bools["sex female"] = false;
        target.Bools["in editor"] = false;
        target.Ints["body version"] = 2;
        target.Ints["Age"] = 20;
        target.Vector3s["Skin Tone"] = new Vector3(0.5f, 0.5f, 0.5f);
        foreach (var k in new[] { "head", "idle stance", "beard", "hair style" }) target.Strings[k] = "";
        if (db.Find(cold.RecordId) is { } record)
            foreach (var r in record.GetReferences("race"))
                if (r.Values.Value0 > 0) { target.References["race"] = [new FcsReference(r.TargetStringId, 0, 0, 0)]; break; }
    }

    /// <summary>
    /// The unique-character table into the CAMERA record (docs/formats/save.md, <c>FUN_1409a8660</c>): <c>num usedUniques</c>, then per entry the CHARACTER id, state,
    /// player flag and handle; unclaimed imprisoned entries (the <c>prisoners</c> uniques) are left out, the load derives them again. The handle is the written character's
    /// when it is one of the player's squads, else the one the loaded save gave it, else null (engine: NPC handles are not tracked back from the writer).
    /// </summary>
    static void WriteUniques(SaveGame game, World world, Dictionary<CharacterId, SaveCharacter> written)
    {
        var record = game.Camera.Record;
        int old = record.Ints.GetValueOrDefault("num usedUniques");
        for (int k = 0; k < old || record.Strings.ContainsKey($"usedUniques{k}"); k++)
        {
            record.Strings.Remove($"usedUniques{k}");
            record.Ints.Remove($"usedUniquesState{k}");
            record.Bools.Remove($"usedUniquesPlayer{k}");
            foreach (var part in new[] { "TYPE", "C", "CS", "I", "S" }) record.Ints.Remove($"usedUniques{k}handle{part}");
        }
        int n = 0;
        foreach (var (id, e) in world.Uniques.All)
        {
            if (!e.Claimed && e.State == UniqueState.Imprisoned) continue;
            record.Strings[$"usedUniques{n}"] = id;
            record.Ints[$"usedUniquesState{n}"] = (int)e.State;
            record.Bools[$"usedUniquesPlayer{n}"] = e.Player;
            var hand = !e.Handle.IsNone && written.TryGetValue(e.Handle, out var sc) && !sc.Handle.IsNull ? sc.Handle : e.SavedHandle;
            hand.Write(record, $"usedUniques{n}handle");
            n++;
        }
        record.Ints["num usedUniques"] = n;
    }

    static void WriteSelection(SaveGame game, World world, Dictionary<CharacterId, SaveCharacter> written)
    {
        var camera = game.Camera;
        var record = camera.Record;
        for (int k = 0; record.Ints.ContainsKey($"selected_characters{k}I"); k++)
            foreach (var part in new[] { "TYPE", "C", "CS", "I", "S" })
                record.Ints.Remove($"selected_characters{k}{part}");
        int n = 0;
        foreach (var id in world.Player.Selection)
        {
            if (!written.TryGetValue(id, out var sc) || sc.Handle.IsNull) continue;
            if (n == 0) camera.SelectedCharacter = sc.Handle;
            sc.Handle.Write(record, $"selected_characters{n}");
            n++;
        }
        if (n == 0) camera.SelectedCharacter = Hand.Null;
    }

    /// <summary>Puts the roaming platoons' positions and AI packages (<c>currentPackage</c>, docs/formats/save.md PLATOON) into their PLATOON records.</summary>
    static void WriteRoaming(SaveGame game, World world, LoadedSave loaded)
    {
        foreach (var (id, name) in loaded.RoamingPlatoons)
        {
            if (world.Platoons.Find(id) is not { } p) continue;
            var platoon = game.Platoons.FirstOrDefault(x => x.Name == name);
            if (platoon is null) continue;
            var at = platoon.Position;
            platoon.Position = new Vector3(p.Position.X, at.Y, p.Position.Y);
            if (p.Package is { } package) platoon.Record.Strings["currentPackage"] = package;
        }
    }
}
