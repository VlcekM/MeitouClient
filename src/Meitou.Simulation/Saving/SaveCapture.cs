using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using SquadMemoryData = Meitou.Simulation.Dialogue.SquadMemoryData;
using FreshMember = (Meitou.Simulation.CharacterCold Cold, System.Numerics.Vector3 Position, float Yaw, Meitou.Simulation.CharacterId Id, bool Leader);

namespace Meitou.Simulation.Saving;

/// <summary>
/// Settings of <see cref="SaveCapture.Capture"/>.
/// </summary>
public sealed class SaveCaptureOptions
{
    /// <summary>
    /// The data files in load order, for the CAMERA <c>mods</c> list.
    /// </summary>
    public IReadOnlyList<string> DataFiles { get; init; } = LoadOrder.BaseFiles;

    /// <summary>
    /// The placed towns of the level data, in order. A new save (no loaded one to carry on) writes a default state for each; without them it has no
    /// town states.
    /// </summary>
    public IReadOnlyList<TownPlacement>? Placements { get; init; }

    /// <summary>
    /// Supplies the stats and medical state of a character when it has none from a loaded save (the host's body systems). Null results fall back to
    /// the saved ones, then to defaults.
    /// </summary>
    public Func<CharacterId, (CharacterStats? Stats, MedicalState? Medical)>? Bodies { get; init; }

    /// <summary>
    /// Seeds the random serials of new handles.
    /// </summary>
    public ulong Seed { get; init; }

    /// <summary>
    /// The level data: the zone files of the visited zones a loaded save has none of are made from it (<see cref="ZoneSave"/>), and a new save's town
    /// states take their level state's handle. Null: no zone files are made and new towns get new handles.
    /// </summary>
    public WorldLevelData? Levels { get; init; }

    /// <summary>
    /// The strategy camera's view, written into CAMERA <c>pos</c> / <c>rot</c> / <c>zoom</c> (docs/formats/camera.md "Saved camera"). Null: the
    /// record keeps what it had.
    /// </summary>
    public SavedView? View { get; init; }
}

/// <summary>
/// A strategy-camera view: the pivot on the ground, yaw (0 looks along -Z), pitch (radians below the horizon) and boom length.
/// </summary>
public readonly record struct SavedView(Vector3 Pivot, float Yaw, float Pitch, float Distance);

/// <summary>
/// Turns a <see cref="World"/> into a <see cref="SaveGame"/> (docs/simulation.md "Saves"). With the <see cref="LoadedSave"/> the world was loaded
/// from, the save it came from is copied and the parts the world models are written over it: clock, money, the factions' relations and prosperity,
/// the player's platoons and their characters (position, facing, stats, medical state, new recruits, removals), the selection, the save's NPC
/// platoons (stand-in positions; the characters of those that were in the world) and the population's own town and roaming squads in the world as new
/// platoons; everything else is carried through unchanged. Without one a new save is made from the game data: all factions with their relations, the
/// player's platoons and the population's squads, a default state per placed town, and empty weather, research and decals. Either way every visited
/// zone gets a zone file (<see cref="ZoneSave"/>).
/// </summary>
public static partial class SaveCapture
{
    #region Capture

    /// <summary>
    /// The save a capture starts from: a copy of the loaded save, or a new one made from the data (all factions with the data's relations, a default
    /// state per placed town). It holds nothing of the world, and costs tens of milliseconds, so a host makes it ahead on a worker and hands it to
    /// <see cref="Capture"/> (which changes it: one use). Reads <paramref name="data"/>'s relations and the loaded save, neither of which a running
    /// world changes.
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

    /// <summary>
    /// <paramref name="prepared"/> is a <see cref="PrepareBase"/> made with the same <paramref name="loaded"/> (null: made here, which is the
    /// slow part).
    /// </summary>
    public static SaveGame Capture(World world, PopulationData data, SaveClock clock, LoadedSave? loaded = null, SaveCaptureOptions? options = null,
        SaveBuilder? prepared = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(data);
        options ??= new SaveCaptureOptions();

        // The item on the mouse is not saved: it goes back into its window first (docs/game/ui-screens.md section 6 "Holding an item").
        world.ReturnHeldItem();

        var builder = prepared ?? PrepareBase(data, loaded, options);
        var game = builder.Game;
        s_memoryPass = new MemoryPass();
        SavedKit.OwnerNotes = s_memoryPass.Owners;

        WriteCamera(game, world, data, clock, options);
        var factionsPopulation = world.Systems.OfType<PopulationSystem>().FirstOrDefault();
        WriteFactionsAndTowns(builder, world, data, loaded, factionsPopulation);
        var (outposts, buildings, campItems) = WriteBuildings(builder, world, data, options);
        var races = new Dictionary<string, RaceData?>(StringComparer.Ordinal);
        var written = WritePlayerSquads(builder, world, data, loaded, options, clock, buildings, races);
        WriteSelection(game, world, written);
        WriteUniques(game, world, written);

        // The NPC platoons: the save's as they are now, and the population's own squads in the world as new platoons (docs/simulation.md "Saves").
        var fresh = WriteNpcPlatoons(builder, world, data, loaded, options, clock, buildings, races);

        // The war states name the campaign platoons by the handles they were just written with.
        WriteWars(game, world, data, loaded, fresh, outposts);

        // The wild: the nests made so far in the town list the original's way, and the wild squads in the world as new platoons of their nest
        // or sector.
        WriteWild(builder, world, data, options, clock, buildings, races, campItems);

        WriteMemories();
        game.UpdateDerived();
        return game;
    }

    /// <summary>
    /// The camera's view, the clock, money, mods, the squad AI settings and the saved camera position.
    /// </summary>
    static void WriteCamera(SaveGame game, World world, PopulationData data, SaveClock clock, SaveCaptureOptions options)
    {
        var camera = game.Camera;
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
    }

    /// <summary>
    /// The factions' state and the towns' knowledge, pools, artifacts and replacements.
    /// </summary>
    static void WriteFactionsAndTowns(SaveBuilder builder, World world, PopulationData data, LoadedSave? loaded, PopulationSystem? population)
    {
        var game = builder.Game;
        UpdateFactions(game, data, loaded, world, population);
        WriteKnownFactions(game, data, world.Player);
        WriteTownKnowledge(builder, data, world.Player);
        if (population is { } pools)
        {
            WriteTownPools(game, data, pools);
            WriteTownArtifacts(game, data, world, pools);
        }

        WriteTownReplacements(game, data);
    }

    /// <summary>
    /// The buildings and their zones: the zone files, the locks, the research, the player's outposts, the machines and the ground items. Returns the
    /// outposts' handles, the building states and the items a camp laid.
    /// </summary>
    static (IReadOnlyDictionary<int, Hand> Outposts, Dictionary<string, FcsRecord> Buildings,
        List<(SaveItem Item, int Camp)> CampItems) WriteBuildings(SaveBuilder builder, World world, PopulationData data, SaveCaptureOptions options)
    {
        var game = builder.Game;

        // The zone files of the zones visited since (before the locks, the player's buildings and the home handles, which write into them).
        ZoneSave.WriteVisited(game, world, data, options.Levels);
        var buildings = HeldSave.BuildingStates(game);
        WriteLocks(game, world, buildings, options.Levels);
        world.Fixtures.WriteResets(buildings);
        ResearchSave.Write(game.Research, world.Research);
        var outposts = data.PlayerFaction >= 0
            ? BuildingSave.Write(game, world, data.Factions[data.PlayerFaction].Id, options.Seed, data.Db)
            : new Dictionary<int, Hand>();

        // Again, with the states of the buildings first written now (job targets name them by handle).
        buildings = HeldSave.BuildingStates(game);
        MachineSave.Write(game, world);
        TownMachineSave.Write(game.Camera.Record, world);
        world.Systems.OfType<Combat.CombatSystem>().FirstOrDefault()?.WriteTurretShots(world, buildings);
        var campItems = GroundItemSave.Write(game, world, data.Items);
        return (outposts, buildings, campItems);
    }

    /// <summary>
    /// The NPC platoons: the loaded roaming platoons, the save's own, and the population's fresh ones. Returns the fresh ones by world platoon id.
    /// </summary>
    static Dictionary<int, SavePlatoon> WriteNpcPlatoons(SaveBuilder builder, World world, PopulationData data, LoadedSave? loaded,
        SaveCaptureOptions options, SaveClock clock, IReadOnlyDictionary<string, FcsRecord> buildings, Dictionary<string, RaceData?> races)
    {
        if (loaded is not null) WriteRoaming(builder.Game, world, loaded);
        WriteSavedNpcs(builder, world, data, options, clock, buildings, races);
        return WriteFreshNpcs(builder, world, data, options, clock, buildings, races, loaded);
    }

    /// <summary>
    /// The war states, which name the campaign platoons by the handles they were just written with.
    /// </summary>
    static void WriteWars(SaveGame game, World world, PopulationData data, LoadedSave? loaded, Dictionary<int, SavePlatoon> fresh,
        IReadOnlyDictionary<int, Hand> outposts)
    {
        if (world.Systems.OfType<PopulationSystem>().FirstOrDefault() is { } wars)
            WarSave.Write(game, world, data, wars, PlatoonHands(game, world, loaded, fresh), outposts);
    }

    /// <summary>
    /// The wild squads and the nests of the population, as new platoons and town states.
    /// </summary>
    static void WriteWild(SaveBuilder builder, World world, PopulationData data, SaveCaptureOptions options, SaveClock clock,
        IReadOnlyDictionary<string, FcsRecord> buildings, Dictionary<string, RaceData?> races, List<(SaveItem Item, int Camp)> campItems)
    {
        if (world.Systems.OfType<PopulationSystem>().FirstOrDefault() is not { } population) return;

        var nests = WriteNests(builder, world, data, population, options.Seed);
        GroundItemSave.WritePersist(builder.Game, data, campItems, nests);
        WriteWildNpcs(builder, world, data, population, nests, options, clock, buildings, races);
    }

    #endregion

    #region Squad memory

    /// <summary>
    /// The platoon's mission town (Meitou key: the TOWN id of the site its package took it to), read back as <see cref="Platoon.Target"/>.
    /// </summary>
    public const string MissionTownKey = "meitou mission town";

    /// <summary>
    /// What a capture collects for the squads' memories: the handle every character written got, and the platoon records to put each squad's memory
    /// on once all are written.
    /// </summary>
    sealed class MemoryPass
    {
        /// <summary>
        /// The handle each character written got.
        /// </summary>
        public readonly Dictionary<CharacterId, Hand> Handles = [];
        /// <summary>
        /// The platoon records to put a squad's memory on, with the memory (null: none).
        /// </summary>
        public readonly List<(FcsRecord Record, SquadMemoryData? Data)> Targets = [];
        /// <summary>
        /// The carry links to write once every character has a handle: a state record and the carried or carrying partner.
        /// </summary>
        public readonly List<(FcsRecord State, CharacterId Partner)> Carries = [];
        /// <summary>
        /// The orders whose target character's handle is written once every character has one: the AI record, the key prefix and the target.
        /// </summary>
        public readonly List<(FcsRecord Record, string Prefix, CharacterId Target)> OrderTargets = [];
        /// <summary>
        /// The jobs on a character (Staying close, Bodyguard) whose target's handle is written once every character has one, so a character new to
        /// this save is named too (#447); one not saved keeps the handle the job was written with.
        /// </summary>
        public readonly List<(FcsRecord Record, string Prefix, CharacterId Target)> JobTargets = [];
        /// <summary>
        /// The saved items whose <c>ownedby</c> names a character.
        /// </summary>
        public readonly List<(SaveItem Item, CharacterId Owner)> Owners = [];
    }

    /// <summary>
    /// The capture running on this thread (null: none).
    /// </summary>
    [ThreadStatic] static MemoryPass? s_memoryPass;

    /// <summary>
    /// The squads' memories of the world's dialogue system; null without one.
    /// </summary>
    static Dialogue.SquadMemory? MemoryOf(World world) => world.Systems.OfType<Dialogue.DialogueSystem>().FirstOrDefault()?.Memory;

    /// <summary>
    /// Puts a squad's memory (null: none, which clears the record's) on a platoon record once the capture has written every
    /// character (<see cref="MemorySave"/>).
    /// </summary>
    static void NoteMemory(FcsRecord record, SquadMemoryData? data) => s_memoryPass?.Targets.Add((record, data));

    /// <summary>
    /// Writes what the capture held back once every character has a handle: the squads' memories, the carry links, the orders' targets and the
    /// items' owners.
    /// </summary>
    static void WriteMemories()
    {
        if (s_memoryPass is not { } pass) return;

        s_memoryPass = null;
        foreach (var (record, data) in pass.Targets)
            MemorySave.Write(record, data, id => pass.Handles.TryGetValue(id, out var hand) ? hand : null);

        // The carry links (FUN_1406280a0 writes the partner's handle under `carrying`, the null one when it is not saved).
        foreach (var (state, partner) in pass.Carries)
            (pass.Handles.TryGetValue(partner, out var hand) ? hand : Hand.Null).Write(state, "carrying");

        // The queued orders name their target character the same way (`tjobT<i>`); one not saved leaves the null handle.
        foreach (var (record, prefix, who) in pass.OrderTargets)
            (pass.Handles.TryGetValue(who, out var hand2) ? hand2 : Hand.Null).Write(record, prefix);
        foreach (var (record, prefix, who) in pass.JobTargets)
        {
            if (pass.Handles.TryGetValue(who, out var hand3)) hand3.Write(record, prefix);
        }

        // Stolen items name the robbed character (`ownedby`); one not saved leaves the null handle the item was written with.
        SavedKit.OwnerNotes = null;
        foreach (var (item, owner) in pass.Owners)
        {
            if (item.Written is { } record && pass.Handles.TryGetValue(owner, out var hand)) hand.Write(record, "ownedby");
        }
    }

    #endregion

    #region Factions and towns

    /// <summary>
    /// The instance of a camp's town state in Meitou saves written before nests were saved the original's way (wild site and TOWN record): still
    /// read, and still updated in a save that has one, never written new (camps are now their nest's state, <see cref="WriteNests"/>).
    /// </summary>
    internal const string CampInstancePrefix = "meitou-nest:";

    /// <summary>
    /// Adds every faction of the data to a new save, each with its relation table (none for the player's), rank and global trust.
    /// </summary>
    static void AddFreshFactions(SaveBuilder builder, PopulationData data)
    {
        for (int a = 0; a < data.Factions.Count; a++)
        {
            var f = data.Factions[a];
            bool player = a == data.PlayerFaction;
            var table = new List<SaveRelation>();
            if (!player)
            {
                for (int b = 0; b < data.Factions.Count; b++)
                    table.Add(new SaveRelation(data.Factions[b].Id, data.Relations.Get(a, b), 0, 0, data.Relations.AtWar(a, b)));
            }

            builder.AddFaction(f.Id, f.Name, player, 1000, table, rank: data.Relations.Rank(a)).GlobalTrust = data.Relations.GlobalTrust(a);
        }
    }

    /// <summary>
    /// Adds the towns of the capture's placements to a new save, with the handles of their level states.
    /// </summary>
    static void AddFreshTowns(SaveBuilder builder, PopulationData data, SaveCaptureOptions options)
    {
        if (options.Placements is null) return;

        foreach (var p in options.Placements)
        {
            if (data.Db.Find(p.TownId) is not { Type: FcsRecordType.Town } record) continue;

            var town = TownData.From(record);

            // The level state's handle, as the original keeps it (docs/formats/save.md "Object handles"): the buildings' and platoons' town handles
            // name it.
            Hand? hand = p.StateId is not null && options.Levels?.Level.Find(p.StateId) is { } level && level.Ints.ContainsKey("handI")
                ? new Hand(level.GetInt("handTYPE"), level.GetInt("handC"), level.GetInt("handCS"), level.GetInt("handI"), level.GetInt("handS"))
                : null;
            builder.AddTown("Town state " + record.Name, p.InstanceId, town.Faction ?? "", isNest: false,
                WorldLayout.ZoneOf(p.Position.X, p.Position.Z), hand: hand);
        }
    }

    /// <summary>
    /// Writes what the player knows of each placed town into its state, by instance id. Flags are only added: the world never forgets a town.
    /// </summary>
    static void WriteTownKnowledge(SaveBuilder builder, PopulationData data, PlayerState player)
    {
        var game = builder.Game;
        var known = player.Towns;
        var byInstance = new Dictionary<string, TownKnowledge>(StringComparer.Ordinal);
        for (int i = 0; i < known.Length && i < data.Sites.Count; i++)
        {
            if (known[i] != 0 && data.Sites[i].InstanceId.Length > 0) byInstance[data.Sites[i].InstanceId] = known[i];
        }

        foreach (var camp in player.Camps)
        {
            string key = CampInstancePrefix + camp.Site.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + camp.TownId;
            byInstance[key] = camp.Known;
        }

        foreach (var town in game.Towns)
        {
            if (!byInstance.TryGetValue(town.Instance, out var flags)) continue;

            if ((flags & TownKnowledge.Discovered) != 0) town.Record.Bools["discovered"] = true;
            if ((flags & TownKnowledge.Explored) != 0) town.Record.Bools["explored"] = true;
            town.Record.Bools["recently discovered"] = (flags & TownKnowledge.Recent) != 0;
        }
    }

    /// <summary>
    /// The towns' pools (<c>pop</c>, <c>popd</c>, <c>pop2</c>, <c>popd2</c>, <c>started</c>, <c>ded</c>, <c>tod</c>) into their states, for each town
    /// made or loaded with pools.
    /// </summary>
    static void WriteTownPools(SaveGame game, PopulationData data, PopulationSystem population)
    {
        var sites = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in data.Sites)
        {
            if (s.InstanceId.Length > 0) sites.TryAdd(s.InstanceId, s.Index);
        }

        foreach (var town in game.Towns)
        {
            if (!town.IsNest && sites.TryGetValue(town.Instance, out int site) && population.PoolsOf(site) is { } pools)
            {
                pools.Write(town.Record);
                town.Record.Bools["started"] = pools.Started;
            }
        }
    }

    /// <summary>
    /// Each placed town's artifacts still to be given out into its state (docs/game/economy.md "Artifacts").
    /// </summary>
    static void WriteTownArtifacts(SaveGame game, PopulationData data, World world, PopulationSystem population)
    {
        var sites = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in data.Sites)
        {
            if (s.InstanceId.Length > 0) sites.TryAdd(s.InstanceId, s.Index);
        }

        foreach (var town in game.Towns)
        {
            if (!town.IsNest && sites.TryGetValue(town.Instance, out int site))
                PopulationSystem.WriteArtifacts(town.Record, population.ArtifactsOf(world, site));
        }
    }

    /// <summary>
    /// Each placed town's <c>replacementTown</c> (the TOWN in its place, "" none) and, while replaced, its <c>faction</c>, into its state.
    /// </summary>
    static void WriteTownReplacements(SaveGame game, PopulationData data)
    {
        var sites = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in data.Sites)
        {
            if (s.InstanceId.Length > 0) sites.TryAdd(s.InstanceId, s.Index);
        }

        foreach (var town in game.Towns)
        {
            if (town.IsNest || !sites.TryGetValue(town.Instance, out int site)) continue;

            var replacement = data.Sites[site].Replacement;
            if (replacement is null && !town.Record.Strings.ContainsKey("replacementTown")) continue;

            town.Record.Strings["replacementTown"] = replacement?.Id ?? "";
            if (replacement is not null && data.Sites[site].Town.Faction is { } faction) town.Record.Strings["faction"] = faction;
        }
    }

    /// <summary>
    /// Writes the factions' prosperity and platoon counters back into the save, and the global trust, rank and relations the world changed
    /// since loading.
    /// </summary>
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

            if (a < loaded.TrustBaseline.Length && data.Relations.GlobalTrust(a) != loaded.TrustBaseline[a])
                f.GlobalTrust = data.Relations.GlobalTrust(a);

            if (f.IsPlayer) continue;

            if (a < loaded.RankBaseline.Length && data.Relations.Rank(a) != loaded.RankBaseline[a]) f.Rank = data.Relations.Rank(a);
            UpdateRelations(f, data, loaded, index, a);
        }
    }

    /// <summary>
    /// The relations of a faction the world changed since loading: a value it changed is written, one it did not touch stays as the save had it.
    /// </summary>
    static void UpdateRelations(SaveFaction f, PopulationData data, LoadedSave loaded, Dictionary<string, int> index, int a)
    {
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
                if (loaded.WarBaseline.Length == loaded.RelationBaseline.Length
                    && data.Relations.AtWar(a, b) != loaded.WarBaseline[a * index.Count + b])
                    war = data.Relations.AtWar(a, b);
            }

            changed |= value != r.Relation || war != r.War;
            updated.Add(r with { Relation = value, War = war });
        }

        if (changed) f.SetRelations(updated);
    }

    /// <summary>
    /// The player faction's <c>known</c> list (on the record a load reads it from, <see cref="SaveGame.PlayerFaction"/>): the factions met, in
    /// faction order, after the ids the game data does not have (kept, as their faction states are).
    /// </summary>
    static void WriteKnownFactions(SaveGame game, PopulationData data, PlayerState player)
    {
        if (game.PlayerFaction is not { } mine) return;

        mine.Known = [.. mine.Known.Where(id => data.FactionIndex(id) < 0), .. player.KnownFactions.Order().Select(f => data.Factions[f].Id)];
    }

    #endregion

    #region Player platoons and characters

    /// <summary>
    /// The player's squads in order, then any other squad holding characters of a loaded platoon, each as its platoon (the loaded one, else a new
    /// one) with its characters; the loaded characters and squads gone since are removed. Returns the characters written by id.
    /// </summary>
    static Dictionary<CharacterId, SaveCharacter> WritePlayerSquads(
        SaveBuilder builder, World world, PopulationData data, LoadedSave? loaded, SaveCaptureOptions options, SaveClock clock,
        IReadOnlyDictionary<string, FcsRecord> buildings, Dictionary<string, RaceData?> races)
    {
        var game = builder.Game;
        var table = world.Characters;
        var written = new Dictionary<CharacterId, SaveCharacter>();
        string playerFactionId = data.PlayerFaction >= 0 ? data.Factions[data.PlayerFaction].Id : FactionData.PlayerId;
        string playerFactionName = world.Player.FactionName ?? (data.PlayerFaction >= 0 ? data.Factions[data.PlayerFaction].Name : "Nameless");
        var playerState = game.Factions.FirstOrDefault(f => f.Id == playerFactionId);
        int squads = 0, members = 0;

        // Platoons of the loaded save whose characters the world held: those not alive any more are removed.
        var spawned = loaded?.SpawnedSlots ?? [];
        var seen = new HashSet<(string Platoon, int Slot)>();

        // The player's squads in order (their `squad index`), then any other squad holding characters of a loaded platoon (a prisoner or slave kept
        // in it).
        var order = SquadManagement.PlayerSquads(world);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var squad in order.Concat(world.Squads.All.Where(s => s.PlatoonId < 0 && !order.Contains(s))))
        {
            // A member of a loaded platoon is written whoever owns it (a prisoner or slave kept in the squad is not the player's but is in the file);
            // only the resident squads the population system made, which have no link, are left out by the player test.
            var alive = squad.Members.Where(m => table.IsAlive(m) && (table.Cold(m.Slot)!.IsPlayer || table.Cold(m.Slot)!.Save is not null)).ToList();
            int index = order.IndexOf(squad);

            // A player's squad is written even when empty: the original keeps empty squads (the squads screen removes them).
            if (alive.Count == 0 && index < 0) continue;

            squads++;
            var platoon = PlayerPlatoon(builder, world, squad, alive, index, kept, playerState, playerFactionName, playerFactionId);
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

                written[id] = WriteCharacter(
                    builder, world, data, options, clock, buildings, races, platoon, sc, id, playerFactionId, squad.Leader == id, orders: true);
            }

            // The platoon's `imprisoned` is set only when it unloads (FUN_1407ef4b0), which these squads do not: the record keeps the save's value.
            SetCharCount(platoon);
        }

        DropGone(builder, spawned, seen);
        DropDeleted(builder, world, loaded, kept);
        game.Camera.Squads = squads;
        game.Camera.Members = members;
        return written;
    }

    /// <summary>
    /// The platoon of a player-side squad: its own, by the file it was loaded from (never by its members: a character moved between squads would
    /// carry the platoon with it), else a new one; placed at its first member, its memory noted, and named as the tabs show it.
    /// </summary>
    static SavePlatoon PlayerPlatoon(SaveBuilder builder, World world, Squad squad, List<CharacterId> alive, int index, HashSet<string> kept,
        SaveFaction? playerState, string playerFactionName, string playerFactionId)
    {
        var game = builder.Game;
        var table = world.Characters;
        string? name = squad.SaveName
            ?? (index < 0 && !squad.Dead ? alive.Select(m => table.Cold(m.Slot)!.Save?.PlatoonName).FirstOrDefault(n => n is not null) : null);
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
            // The name the tabs show is the record's name; the string id stays the file's (Observed: rename writes the record's name,
            // docs/formats/save.md).
            if (squad.Name.Length > 0) platoon.Record.Name = squad.Name;
            platoon.Record.Ints["squad index"] = index;
        }
        else if (squad.Dead)
        {
            platoon.Record.Name = SquadManagement.DeadSquadName;
        }

        return platoon;
    }

    /// <summary>
    /// Characters of a loaded platoon that the world had and now has no more (killed, left the squad): gone from the file.
    /// </summary>
    static void DropGone(SaveBuilder builder, HashSet<(string Platoon, int Slot)> spawned, HashSet<(string Platoon, int Slot)> seen)
    {
        var game = builder.Game;
        foreach (var (platoonName, slot) in spawned)
        {
            if (seen.Contains((platoonName, slot))) continue;

            var platoon = game.Platoons.FirstOrDefault(p => p.Name == platoonName);
            var sc = platoon?.Characters.FirstOrDefault(c => c.Handle.I == slot);
            if (platoon is not null && sc is not null) builder.RemoveCharacter(platoon, sc);
        }
    }

    /// <summary>
    /// A loaded player squad removed since (DELETE on the squads screen): its platoon goes, as the original destroys it (FUN_1406ba9d0).
    /// </summary>
    static void DropDeleted(SaveBuilder builder, World world, LoadedSave? loaded, HashSet<string> kept)
    {
        var game = builder.Game;
        foreach (var lp in loaded?.PlayerPlatoons ?? [])
        {
            if (!kept.Contains(lp.Platoon.Name) && world.Squads.Find(lp.Squad.Id) is null
                && game.Platoons.FirstOrDefault(p => p.Name == lp.Platoon.Name) is { } gone)
            {
                builder.RemovePlatoon(gone);
            }
        }
    }

    /// <summary>
    /// The next free <c>&lt;faction name&gt;_&lt;n&gt;</c> platoon name of a faction, counting its platoon counter on.
    /// </summary>
    static string FreshName(SaveGame game, SaveFaction? faction, string factionName)
    {
        int n = faction?.PlatoonCounter ?? 0;
        while (game.Platoons.Any(p => p.Name == $"{factionName}_{n}")) n++;

        if (faction is not null) faction.PlatoonCounter = n + 1;
        return $"{factionName}_{n}";
    }

    /// <summary>
    /// Sets the platoon's character count on the platoon and its collection records.
    /// </summary>
    static void SetCharCount(SavePlatoon platoon)
    {
        platoon.CharCount = platoon.Characters.Count;
        if (platoon.Collection is { } c) c.Ints["char count"] = platoon.Characters.Count;
        platoon.Record.Ints["char count"] = platoon.Characters.Count;
    }

    /// <summary>
    /// One character as it is in the world into its platoon file: its records (<paramref name="sc"/>), or new ones when null. Place and facing, name,
    /// leader flag, bounties, crime marks, the player's orders (<paramref name="orders"/>, the player's squads), bed or cage; stats and medical state
    /// (from the host, else the loaded ones, else defaults for a new one); a new one's look; what it carries.
    /// </summary>
    static SaveCharacter WriteCharacter(
        SaveBuilder builder, World world, PopulationData data, SaveCaptureOptions options, SaveClock clock,
        IReadOnlyDictionary<string, FcsRecord> buildings,
        Dictionary<string, RaceData?> races, SavePlatoon platoon, SaveCharacter? sc, CharacterId id, string ownerFactionId, bool isLeader, bool orders)
    {
        ref readonly var hot = ref world.Characters.Previous[id.Slot];
        return WriteCharacter(
            builder, world, data, options, clock, buildings, races, platoon, sc, world.Characters.Cold(id.Slot)!, hot.Position, hot.Yaw, id,
            ownerFactionId,
            isLeader, orders);
    }

    /// <summary>
    /// The same for a character as <paramref name="cold"/> standing at <paramref name="position"/> facing <paramref name="yaw"/>: one in the world
    /// (<paramref name="id"/>) or a stored one (none).
    /// </summary>
    static SaveCharacter WriteCharacter(
        SaveBuilder builder, World world, PopulationData data, SaveCaptureOptions options, SaveClock clock,
        IReadOnlyDictionary<string, FcsRecord> buildings,
        Dictionary<string, RaceData?> races, SavePlatoon platoon, SaveCharacter? sc, CharacterCold cold, Vector3 position, float yaw, CharacterId id,
        string ownerFactionId, bool isLeader, bool orders)
    {
        var hot = (Position: position, Yaw: yaw);
        bool isNew = sc is null;
        sc ??= builder.AddCharacter(platoon, cold.RecordId, cold.Name, ownerFactionId, hot.Position, SaveRotation.OfYaw(hot.Yaw), isLeader: isLeader,
            squadMemberType: cold.Role);

        // The handle's own serial is "random" in the original, and the loader rolls a character's look from it: a character with a rolled look gets
        // the look's seed as its serial, so loading a save without its items (one written before inventories were) rolls the same face, hair and
        // clothes again.
        if (isNew && cold.Appearance?.Loadout is { } loadout) sc.Handle = sc.Handle with { S = loadout.Seed };

        // Where and which way.
        sc.Position = hot.Position;
        var link = cold.Save;
        sc.Rotation = link is not null && MathF.Abs(SaveRotation.Difference(hot.Yaw, link.Yaw)) < 1e-4f ? link.Rotation : SaveRotation.OfYaw(hot.Yaw);
        if (sc.GameState is { } g) WriteGameState(g, builder, world, data, options, clock, buildings, sc, cold, id, isLeader, orders, link);

        // Body: stats and medical state, from the host, else the loaded ones, else (for a new character) defaults.
        var (stats, medical) = (id.IsNone ? null : options.Bodies?.Invoke(id)) ?? (null, null);
        stats ??= cold.Stats ?? link?.Stats;
        medical ??= cold.Medical ?? link?.Medical;
        WriteBody(sc, cold.RecordId, stats, medical, isNew, data, races);
        if (isNew || cold.LookEdited) WriteAppearance(sc, cold, data.Db, races, replace: !isNew);

        // What it carries and wears, as it is now; items read from a save keep their records' other keys.
        if (cold.Inventory is { } inventory) builder.SetInventory(platoon, sc, SavedKit.FromInventory(inventory));
        if (!id.IsNone) s_memoryPass?.Handles[id] = sc.Handle;
        return sc;
    }

    /// <summary>
    /// The character's GAMESTATE_CHARACTER keys: name, leader, bounties, crime marks, slave, dialogue, orders, jobs, queued orders, held state,
    /// carrying, ragdoll, portrait and age.
    /// </summary>
    static void WriteGameState(FcsRecord g, SaveBuilder builder, World world, PopulationData data, SaveCaptureOptions options, SaveClock clock,
        IReadOnlyDictionary<string, FcsRecord> buildings, SaveCharacter sc, CharacterCold cold, CharacterId id, bool isLeader, bool orders,
        SavedCharacterLink? link)
    {
        g.Strings["name"] = cold.Name;
        g.Bools["is leader"] = isLeader;
        BountySave.Write(g, cold.Bounties, data.Relations, clock, world.Tick, world.TickSeconds);
        CrimeSave.Write(g, cold);
        SlaveSave.Write(g, cold, data);
        DialogueSave.Write(g, cold);
        if (orders) Orders.Write(g, cold);
        if (orders && sc.Ai is { } ai)
        {
            PermaJobSave.Write(ai, cold, world.Buildings, buildings, who => HandOf(world, who));
            DeferJobTargets(ai, cold);
        }

        if (orders && sc.Ai is { } queueAi)
        {
            OrderSave.Write(queueAi, cold, world, buildings, (record, prefix, who) =>
            {
                if (s_memoryPass is { } pass) pass.OrderTargets.Add((record, prefix, who));   // the handle is written once every character has one
                else if (world.Characters.TryResolveNext(who, out int slot) && world.Characters.Cold(slot)?.Save is { } held)
                    held.Source.Handle.Write(record, prefix);
            });
        }

        HeldSave.Write(g, cold, link, world.Fixtures, buildings);

        // Carrying or carried: the bool says which, the partner's handle is written once every character has one.
        g.Bools["carrying"] = !cold.Carrying.IsNone;
        var partner = cold.Carrying.IsNone ? cold.CarriedBy : cold.Carrying;
        if (partner.IsNone) Hand.Null.Write(g, "carrying");
        else s_memoryPass?.Carries.Add((g, partner));

        // A world without ragdolls (headless) keeps what was read; a stale pose is not used where the character no longer lies.
        if (id.IsNone) RagdollSave.Write(g, null);
        else if (world.Systems.OfType<Ragdolls.RagdollSystem>().FirstOrDefault() is { } ragdolls) RagdollSave.Write(g, ragdolls.SaveState(id));

        if (cold.PortraitSerial is { } portraitSerial) g.Ints["portrait_serial"] = (int)portraitSerial;

        // The character's slot 0x398 (FUN_1406280a0): an animal's age, 1 for people (docs/game/animals.md "Ageing"; Verified in vanilla saves).
        if ((cold.Stats ?? link?.Stats) is { } aged) g.Floats["age"] = aged.Scale;
    }

    /// <summary>
    /// Holds back the target handles of the character's jobs on a character until every character has one (<see cref="MemoryPass.JobTargets"/>).
    /// </summary>
    static void DeferJobTargets(FcsRecord ai, CharacterCold cold)
    {
        if (s_memoryPass is not { } pass) return;

        for (int i = 0; i < cold.Jobs.Count; i++)
        {
            var job = cold.Jobs[i];
            if (job.Site is null && !job.Character.IsNone) pass.JobTargets.Add((ai, PermaJobSave.TargetKey(i), job.Character));
        }
    }

    /// <summary>
    /// The handle of a queued job's target character: the one it was written with, or the null handle when it is not in the world or has no save
    /// link yet (one recruited since the load; <see cref="DeferJobTargets"/> covers that).
    /// </summary>
    static Hand HandOf(World world, CharacterId who) =>
        world.Characters.TryResolveNext(who, out int target) && world.Characters.Cold(target)?.Save is { } link ? link.Source.Handle : Hand.Null;

    /// <summary>
    /// The <c>warrior spirit</c> of the character record's STATS template (0 without one or without the field).
    /// </summary>
    static float WarriorSpiritOf(GameDatabase db, string recordId) =>
        db.Find(recordId)?.GetReferences("stats").Where(r => r.Values.Value0 > 0).Select(r => db.Find(r.TargetStringId))
            .FirstOrDefault(s => s?.Type == FcsRecordType.Stats)?.GetFloat("warrior spirit") ?? 0;

    /// <summary>
    /// Writes a character's stats and medical state into its records (a new character gets fresh ones).
    /// </summary>
    static void WriteBody(SaveCharacter sc, string recordId, CharacterStats? stats, MedicalState? medical, bool isNew, PopulationData data,
        Dictionary<string, RaceData?> races)
    {
        if (stats is null && isNew) stats = new CharacterStats();
        if (stats is not null && sc.Stats is { } sr)
        {
            stats.WriteSave(sr.Floats);
            LegacyStats.Fill(sr, isNew ? WarriorSpiritOf(data.Db, recordId) : 0);
        }

        if (medical is null && isNew && RaceOf(recordId, data.Db, races) is { } race)
            medical = MedicalState.Create(race, stats?.Strength ?? 50);

        if (medical is not null && sc.Medical is { } mr)
        {
            medical.WriteSave(mr.Floats, mr.Bools, mr.Strings);
            if (sc.Platoon.File is { } file) SavedLimbs.Write(medical, file, mr);
        }
    }

    /// <summary>
    /// The race of a character record (cached by id); null when it has none.
    /// </summary>
    static RaceData? RaceOf(string recordId, GameDatabase db, Dictionary<string, RaceData?> cache)
    {
        if (db.Find(recordId) is not { } record || CharacterAssembly.RaceId(record) is not { } id) return null;
        return CharacterAssembly.RaceDataOf(db, id, cache);
    }

    /// <summary>
    /// The CHARACTER_APPEARANCE of a new character: the generated one when the character has a look (its sliders, head, hair, skin tone), else only
    /// the race and the editor flags. With <paramref name="replace"/> (a loaded character edited in the character editor) the record's keys become
    /// the look's exactly: the editor starts from a copy of the saved record, so the original's key set and order are kept. The loader reads the
    /// record back into the look (<see cref="SavedKit"/>).
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
                target.Bools.Clear();
                target.Floats.Clear();
                target.Ints.Clear();
                target.Vector3S.Clear();
                target.Strings.Clear();
                target.References.Clear();
            }

            foreach (var (k, v) in source.Bools) target.Bools[k] = v;
            foreach (var (k, v) in source.Floats) target.Floats[k] = v;
            foreach (var (k, v) in source.Ints) target.Ints[k] = v;
            foreach (var (k, v) in source.Vector3S) target.Vector3S[k] = v;
            foreach (var (k, v) in source.Strings) target.Strings[k] = v;
            foreach (var (k, v) in source.References) target.References[k] = [.. v];
            return;
        }

        target.Bools["sex female"] = false;
        target.Bools["in editor"] = false;
        target.Ints["body version"] = 2;
        target.Ints["Age"] = 20;
        target.Vector3S["Skin Tone"] = new Vector3(0.5f, 0.5f, 0.5f);
        foreach (var k in new[] { "head", "idle stance", "beard", "hair style" }) target.Strings[k] = "";
        if (db.Find(cold.RecordId) is { } record)
        {
            foreach (var r in record.GetReferences("race"))
            {
                if (r.Values.Value0 > 0)
                {
                    target.References["race"] = [new FcsReference(r.TargetStringId, 0, 0, 0)];
                    break;
                }
            }
        }
    }

    #endregion

    #region NPC platoons

    /// <summary>
    /// The container serial of every zone container (docs/formats/save.md "Object handles").
    /// </summary>
    const int ZoneContainerSerial = 11111;

    /// <summary>
    /// The save's NPC platoons (docs/simulation.md "Saves"): a stand-in that travelled gets its position and package; one whose characters were in
    /// the world gets them as they are (in the world now) or were (when it left): place, facing, body, kit, bounties; a character whose body is gone
    /// leaves the file.
    /// </summary>
    static void WriteSavedNpcs(
        SaveBuilder builder, World world, PopulationData data, SaveCaptureOptions options, SaveClock clock,
        IReadOnlyDictionary<string, FcsRecord> buildings,
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
            WriteSavedMembers(builder, world, data, options, clock, buildings, races, platoon, sp, squad);
            SetCharCount(platoon);
        }
    }

    /// <summary>
    /// The characters of a save's NPC platoon: a corpse gone since leaves the file, one in the world is written as it is, one that left as it was.
    /// </summary>
    static void WriteSavedMembers(
        SaveBuilder builder, World world, PopulationData data, SaveCaptureOptions options, SaveClock clock,
        IReadOnlyDictionary<string, FcsRecord> buildings,
        Dictionary<string, RaceData?> races, SavePlatoon platoon, SavedPlatoon sp, Squad? squad)
    {
        var table = world.Characters;
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
            {
                WriteCharacter(builder, world, data, options, clock, buildings, races, platoon, sc, m.Live, sc.OwnerFactionId,
                    squad?.Leader == m.Live, orders: false);
            }
            else if (m.Position is { } at)
            {
                WriteLeftCharacter(builder, world, data, clock, races, platoon, sc, m, at);
            }
        }
    }

    /// <summary>
    /// A character of a saved platoon that left the world: written as it was when its platoon left (place, facing, body, bounties and kit).
    /// </summary>
    static void WriteLeftCharacter(SaveBuilder builder, World world, PopulationData data, SaveClock clock, Dictionary<string, RaceData?> races,
        SavePlatoon platoon, SaveCharacter sc, SavedMember m, Vector3 at)
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

    /// <summary>
    /// The PLATOON handle of every world platoon the save holds: the loaded save's (stand-ins and saved platoons, by name; not those despawned) and
    /// the ones just written (<paramref name="fresh"/>).
    /// </summary>
    static Dictionary<int, Hand> PlatoonHands(SaveGame game, World world, LoadedSave? loaded, Dictionary<int, SavePlatoon> fresh)
    {
        var byName = new Dictionary<string, SavePlatoon>(StringComparer.Ordinal);
        foreach (var x in game.Platoons) byName.TryAdd(x.Name, x);

        var hands = new Dictionary<int, Hand>();
        if (loaded is not null)
        {
            foreach (var (id, name) in loaded.RoamingPlatoons)
            {
                if (byName.TryGetValue(name, out var sp)) hands[id] = sp.Handle;
            }
        }

        if (world.Saved is { } saved)
        {
            foreach (var sp in saved.Platoons)
            {
                if (sp.PlatoonId >= 0 && !sp.Despawned && byName.TryGetValue(sp.Name, out var written)) hands[sp.PlatoonId] = written.Handle;
            }
        }

        foreach (var (id, sp) in fresh) hands[id] = sp.Handle;
        return hands;
    }

    /// <summary>
    /// The NPC squads the population made (a town's residents and bar squads, in the world or stored since their town unloaded, and roaming squads
    /// while they are in the world) as new platoons, so a save keeps them where they are: a new PLATOON with its <c>basetown</c>, <c>is resident</c>
    /// for a town's residents and the AI package running; its characters as new ones (look, kit, body). A saved never-made platoon, wild or of a
    /// town, is written into its original record once made. Other wild squads use WriteWildNpcs; roaming stand-ins without characters are skipped,
    /// except a campaign's (its war state names it). Returns the platoons written by world platoon id.
    /// </summary>
    static Dictionary<int, SavePlatoon> WriteFreshNpcs(
        SaveBuilder builder, World world, PopulationData data, SaveCaptureOptions options, SaveClock clock,
        IReadOnlyDictionary<string, FcsRecord> buildings,
        Dictionary<string, RaceData?> races, LoadedSave? loaded) =>
        new FreshNpcWriter(builder, world, data, options, clock, buildings, races).Write(loaded);

    /// <summary>
    /// Writes the population's NPC squads as new platoons (<see cref="WriteFreshNpcs"/>): the squads in the world, those stored since they left it,
    /// then a campaign's stand-ins.
    /// </summary>
    sealed class FreshNpcWriter
    {
        /// <summary>
        /// The save being built.
        /// </summary>
        readonly SaveBuilder _builder;

        /// <summary>
        /// The world saved.
        /// </summary>
        readonly World _world;

        /// <summary>
        /// The population's data.
        /// </summary>
        readonly PopulationData _data;

        /// <summary>
        /// The capture's options.
        /// </summary>
        readonly SaveCaptureOptions _options;

        /// <summary>
        /// The save's clock.
        /// </summary>
        readonly SaveClock _clock;

        /// <summary>
        /// The buildings' saved states, by instance.
        /// </summary>
        readonly IReadOnlyDictionary<string, FcsRecord> _buildings;

        /// <summary>
        /// The races' data, by record id.
        /// </summary>
        readonly Dictionary<string, RaceData?> _races;

        /// <summary>
        /// The save's game.
        /// </summary>
        readonly SaveGame _game;

        /// <summary>
        /// The save's factions, by id (the first of each).
        /// </summary>
        readonly Dictionary<string, SaveFaction> _factions = new(StringComparer.Ordinal);

        /// <summary>
        /// The save's towns' handles, by town instance.
        /// </summary>
        readonly Dictionary<string, Hand> _townHands = new(StringComparer.Ordinal);

        /// <summary>
        /// The save's platoons, by name (the first of each).
        /// </summary>
        readonly Dictionary<string, SavePlatoon> _byName = new(StringComparer.Ordinal);

        /// <summary>
        /// The platoons written, by world platoon id.
        /// </summary>
        readonly Dictionary<int, SavePlatoon> _written = [];

        /// <summary>
        /// A writer for the save being built: the save's factions, town handles and platoons indexed.
        /// </summary>
        public FreshNpcWriter(SaveBuilder builder, World world, PopulationData data, SaveCaptureOptions options, SaveClock clock,
            IReadOnlyDictionary<string, FcsRecord> buildings, Dictionary<string, RaceData?> races)
        {
            (_builder, _world, _data, _options, _clock, _buildings, _races) = (builder, world, data, options, clock, buildings, races);
            _game = builder.Game;
            foreach (var f in _game.Factions) _factions.TryAdd(f.Id, f);
            foreach (var t in _game.Towns)
            {
                if (t.Instance.Length > 0 && !t.Handle.IsNull) _townHands.TryAdd(t.Instance, t.Handle);
            }

            foreach (var x in _game.Platoons) _byName.TryAdd(x.Name, x);
        }

        /// <summary>
        /// Writes the squads in the world, the stored ones and the campaign's stand-ins; returns the platoons written by world platoon id.
        /// </summary>
        public Dictionary<int, SavePlatoon> Write(LoadedSave? loaded)
        {
            WriteWorldSquads();
            WriteStoredSquads();
            WriteStandIns(loaded);
            return _written;
        }

        /// <summary>
        /// A save's never-made platoon once made holds its squad in Stored beside its record (Platoon.Unmade).
        /// </summary>
        SavePlatoon? Into(Platoon? p) => p is { Saved: { } saved, Stored: not null } ? _byName.GetValueOrDefault(saved.Name) : null;

        /// <summary>
        /// Whether a squad of the town and faction is written: a known town, a known faction not the player's.
        /// </summary>
        bool Writable(int town, int faction) =>
            town >= 0 && town < _data.Sites.Count && faction >= 0 && faction < _data.Factions.Count && faction != _data.PlayerFaction;

        /// <summary>
        /// A saved wild platoon made from its template has no placed town, but its generated characters belong in its original record.
        /// </summary>
        bool MadeWild(Platoon? platoon, int faction) =>
            platoon is { Wild: true, Stored: not null } && Into(platoon) is not null
            && faction >= 0 && faction < _data.Factions.Count && faction != _data.PlayerFaction;

        /// <summary>
        /// The squads in the world (not a save's platoon), with their dead; a made never-made platoon wiped out keeps its record unless the original
        /// would drop it.
        /// </summary>
        void WriteWorldSquads()
        {
            var table = _world.Characters;
            foreach (var squad in _world.Squads.All)
            {
                Platoon? roamer = null;
                if (squad.PlatoonId >= 0)
                {
                    roamer = _world.Platoons.Find(squad.PlatoonId);
                    if (roamer is null || roamer.Saved is not null && roamer.Stored is null) continue;   // a save's platoon (WriteSavedNpcs)
                }

                if (!Writable(squad.Town, squad.Faction) && !MadeWild(roamer, squad.Faction)) continue;

                // The dead stay in the platoon's list as the original writes it (FUN_1404ff410, docs/game/character-stats.md "Corpses").
                var every = squad.Members.Where(m => table.IsAlive(m) && table.Cold(m.Slot) is { IsPlayer: false, Save: null }).ToList();
                var alive = every.Where(m => table.Cold(m.Slot)!.DiedTick < 0).ToList();
                var into = Into(roamer);
                if (alive.Count == 0)
                {
                    // A save's never-made platoon once made, wiped out, keeps its record: the original's saver (FUN_140372ef0) drops a declared-dead
                    // platoon unless a member is carried or a player's squad is within 500, else writes it with its dead and `never been activated` 0
                    // (a regenerating one too).
                    if (into is null) continue;

                    if (roamer!.Dead && !every.Any(m => !table.Cold(m.Slot)!.CarriedBy.IsNone) && !PlayerNear(_world, roamer.Position))
                    {
                        _builder.RemovePlatoon(into);
                        continue;
                    }
                }

                var leader = alive.Contains(squad.Leader) || alive.Count == 0 ? squad.Leader : alive[0];
                var members = every
                    .Select(id => (table.Cold(id.Slot)!, table.Previous[id.Slot].Position, table.Previous[id.Slot].Yaw, id, id == leader))
                    .ToList();
                var sp = WriteWorldSquad(squad, roamer, members, into);
                if (roamer is not null) _written[roamer.Id] = sp;
            }
        }

        /// <summary>
        /// Writes one squad in the world, with its platoon's state when it has one.
        /// </summary>
        SavePlatoon WriteWorldSquad(Squad squad, Platoon? roamer, List<FreshMember> members, SavePlatoon? into)
        {
            return WritePlatoon(
                squad.Town, squad.Faction, squad.TemplateId, squad.Money, roamer is { Pinned: false }, squad.HomeBuilding,
                roamer?.Package ?? squad.Ai?.Current?.Id,
                members, squad.Shop, ai: AiSave.Of(squad.Ai) ?? squad.SavedAi, memory: MemoryOf(_world)?.Snapshot(squad.Id),
                imprisoned: roamer?.Imprisoned ?? false, dead: roamer?.Dead ?? false, intact: roamer?.IntactFlag ?? squad.Ai?.Intact ?? true,
                at: roamer is null ? default : Ground(_world, roamer.Position), into: into,
                missionTown: squad.Ai is { } board && Ai.AiSystem.BoardMoves(board) && PopulationSystem.MissionSite(board) is >= 0 and var mission
                    ? mission
                    : roamer?.Target ?? -1);
        }

        /// <summary>
        /// Squads stored since they left the world: a town's since its town unloaded, a roaming one (Meitou) since it first left.
        /// </summary>
        void WriteStoredSquads()
        {
            foreach (var p in _world.Platoons.All)
            {
                if (p is not { State: PlatoonState.Unloaded, Stored: { } stored }) continue;
                if (!Writable(p.Origin, p.Faction) && !MadeWild(p, p.Faction)) continue;

                // A made never-made one with no one left (the dead are not stored) keeps its record too, but declared dead: the original drops an
                // unloaded dead platoon.
                var into = Into(p);
                if (stored.Members.Count == 0 && (into is null || p.Dead))
                {
                    if (into is not null) _builder.RemovePlatoon(into);
                    continue;
                }

                var members = stored.Members.Concat(stored.Dead).Select(m => (m.Cold, m.Position, m.Yaw, CharacterId.None, m.Leader)).ToList();
                _written[p.Id] = WritePlatoon(
                    p.Origin, p.Faction, p.TemplateId, p.Money, !stored.Pinned, stored.HomeBuilding, p.Package ?? stored.Package, members, stored.Shop,
                    Ground(_world, p.Position), ai: stored.Ai, memory: stored.Memory, imprisoned: p.Imprisoned, dead: p.Dead, intact: p.IntactFlag,
                    into: into, missionTown: p.Target);
                if (p.Persistent) _written[p.Id].Record.Bools["persistent"] = true;   // a split-off prisoner's platoon
            }
        }

        /// <summary>
        /// A campaign's stand-ins that never had their characters (not a save's: those are in it already).
        /// </summary>
        void WriteStandIns(LoadedSave? loaded)
        {
            foreach (var p in _world.Platoons.All)
            {
                if (p is not { Campaign: not null, State: PlatoonState.Unloaded, Saved: null } || p.Stored is { Members.Count: > 0 }
                    || !Writable(p.Origin, p.Faction))
                {
                    continue;
                }

                if (loaded?.RoamingPlatoons.ContainsKey(p.Id) == true) continue;

                _written[p.Id] = WritePlatoon(
                    p.Origin, p.Faction, p.TemplateId, p.Money, true, null, p.Package, [], null, Ground(_world, p.Position), imprisoned: p.Imprisoned,
                    dead: p.Dead, intact: p.IntactFlag, missionTown: p.Target);
            }
        }

        /// <summary>
        /// A new record, or (<paramref name="into"/>) a save's never-made platoon Meitou made from its template (Platoon.Unmade): its own record
        /// written back, as the original's saver does (FUN_140372ef0: <c>never been activated</c> 0, its file and char count); its pool, residency,
        /// town and home stay as the record has them.
        /// </summary>
        SavePlatoon WritePlatoon(int town, int factionIndex, string templateId, long money, bool roaming, TownBuilding? homeBuilding, string? package,
            List<FreshMember> members, Trading.Shop? shop, Vector3 at = default,
            AiSave? ai = null, SquadMemoryData? memory = null, bool imprisoned = false, bool dead = false, bool intact = true, SavePlatoon? into = null,
            int missionTown = -1)
        {
            var faction = _data.Factions[factionIndex];
            var lead = members.FirstOrDefault(m => m.Leader);
            if (lead.Cold is null && members.Count > 0) lead = members[0];
            if (members.Count == 0) lead.Position = at;

            var platoon = PlatoonRecord(faction, templateId, money, members.Count, lead.Position, into);
            var record = platoon.Record;

            if (into is null) SetTown(record, town, templateId, roaming, homeBuilding, package);

            record.Bools["imprisoned"] = imprisoned;
            record.Bools["dead"] = dead;
            record.Bools["intact"] = intact;

            if (package is not null) record.Strings["currentPackage"] = package;
            if (missionTown >= 0 && missionTown != town && missionTown < _data.Sites.Count)
                record.Strings[MissionTownKey] = _data.Sites[missionTown].Town.Id;
            else record.Strings.Remove(MissionTownKey);
            ShopSave.Write(record, shop, _world);
            AiSave.Write(record, ai);
            NoteMemory(record, memory);

            WriteMapArea(record, _data, lead.Position);
            WriteMembers(platoon, faction, members, lead.Cold);
            SetCharCount(platoon);

            return platoon;
        }

        /// <summary>
        /// A new town platoon's pool and home handles. Existing records keep their town metadata, including a wild one's nest or lack of a town.
        /// </summary>
        void SetTown(FcsRecord record, int town, string templateId, bool roaming, TownBuilding? homeBuilding, string? package)
        {
            var site = _data.Sites[town];

            // The TOWN the town is now (FUN_1404ffad0 compares it with the town's replacement).
            record.Strings["basetown"] = site.Replacement?.Id ?? site.Record.Id;
            record.Bools[SavedPlatoon.BaseTownKey] = true;
            MarkPools(record, town, site, templateId, roaming, homeBuilding, package);

            // Its town and home building by handle, as the original names them (docs/formats/save.md "NPC platoons").
            if (_townHands.TryGetValue(site.InstanceId, out var townHand)) townHand.Write(record, "hometown");
            if (homeBuilding is { } home && HomeHandle(_game, home) is { } homeHand) homeHand.Write(record, "homebuilding");
        }

        /// <summary>
        /// The platoon's record: the made never-made one given its file back, else a new one (a stand-in with no characters gets no file).
        /// </summary>
        SavePlatoon PlatoonRecord(FactionData faction, string templateId, long money, int count, Vector3 at, SavePlatoon? into)
        {
            int purse = (int)Math.Clamp(money, int.MinValue, int.MaxValue);
            if (into is not null)
            {
                _builder.GiveFile(into);
                into.Position = at;
                into.Money = purse;
                into.Record.Bools["never been activated"] = false;
                return into;
            }

            _factions.TryGetValue(faction.Id, out var state);
            var platoon = _builder.AddPlatoon(FreshName(_game, state, faction.Name), faction.Name, faction.Id, templateId, at, purse);
            if (count == 0)
            {
                // A stand-in with no characters: no platoon file, as the original writes a platoon that is not loaded (`content file` "platoon/").
                platoon.File = null;
                platoon.ContentFile = "platoon/";
            }

            return platoon;
        }

        /// <summary>
        /// A new platoon's pool, persistence and residency.
        /// </summary>
        void MarkPools(FcsRecord record, int town, TownSite site, string templateId, bool roaming, TownBuilding? homeBuilding, string? package)
        {
            int siteFaction = _data.SiteFactions.Count > town ? _data.SiteFactions[town] : -1;

            // `sqt` is the town pool it counts against: 1 residents (and bar squads), 2 roaming (docs/formats/save.md "NPC platoons"). A template
            // with `roaming military` is made in mode 2 even from a town's resident list (FUN_140583a10, Verified), so its housing squads are
            // roaming-pool platoons too.
            record.Ints["sqt"] = roaming || _data.Db.Find(templateId)?.GetBool("roaming military") == true ? 2 : 1;

            // Residents and bar squads are persistent (the original keeps them off screen, docs/game/factions-squads-towns.md "Persistent
            // platoons"), roaming ones not but while they occupy a town.
            record.Bools["persistent"] = !roaming || Ai.AiSystem.Occupies(_data.Ai, package);

            // A squad with a home is a resident (every platoon of the samples with a file and a `homebuilding` has `is resident`, and only
            // those): a default resident too.
            record.Bools["is resident"] = !roaming && (homeBuilding is not null
                || ResidentHomes.Entries(site.Town, siteFaction >= 0 ? _data.Factions[siteFaction] : null).Any(e => e.Id == templateId));
        }

        /// <summary>
        /// Writes the platoon's characters as new ones, each owned by its own faction (else the platoon's).
        /// </summary>
        void WriteMembers(SavePlatoon platoon, FactionData faction, List<FreshMember> members, CharacterCold? lead)
        {
            foreach (var m in members)
            {
                int f = m.Cold.Faction;
                string owner = f >= 0 && f < _data.Factions.Count ? _data.Factions[f].Id : faction.Id;
                WriteCharacter(
                    _builder, _world, _data, _options, _clock, _buildings, _races, platoon, null, m.Cold, m.Position, m.Yaw, m.Id, owner,
                    ReferenceEquals(m.Cold, lead), orders: false);
            }
        }
    }

    /// <summary>
    /// A stand-in's place (X, Z) on the ground.
    /// </summary>
    static Vector3 Ground(World world, Vector2 at) => new(at.X, world.Walkability.GroundHeight(at.X, at.Y), at.Y);

    /// <summary>
    /// Whether a squad of the player's is within 500 of a place (X, Z): the original's saver keeps a dead platoon then (<c>FUN_140372ef0</c>,
    /// 250000 squared).
    /// </summary>
    static bool PlayerNear(World world, Vector2 at)
    {
        var table = world.Characters;
        foreach (var s in world.Squads.All)
        {
            if (s.Faction == world.Player.Faction && !s.Dead && table.TryResolveNext(s.Leader, out int slot)
                && Vector2.DistanceSquared(new Vector2(table.Previous[slot].Position.X, table.Previous[slot].Position.Z), at) < 500f * 500f)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The handle a platoon's <c>homebuilding</c> names a town building by: its state's <c>handle</c> when the save's zone file holds the building,
    /// else the handle of its level-data state, which the original gives the building when it loads a zone that has no saved state of it
    /// (docs/formats/save.md "Object handles"). Null when it has neither.
    /// </summary>
    static Hand? HomeHandle(SaveGame game, TownBuilding b)
    {
        if (game.Zones.TryGetValue(b.Zone, out var zone) && zone.BuildingList is { } list)
        {
            var entry = list.Instances.FirstOrDefault(i => i.Id == b.InstanceId);
            if (entry is not null
                && zone.File.Records.FirstOrDefault(r => r.RecordType == FcsRecordType.GamestateBuilding
                    && entry.States.Contains(r.StringId)) is { } state
                && Hand.Read(state, "handle") is { IsNull: false } saved)
                return saved;
        }

        return b.HandleSlot >= 0
            ? new Hand((int)FcsRecordType.Building, SaveZone.ContainerIdOf(b.Zone), ZoneContainerSerial, b.HandleSlot, b.HandleSerial)
            : null;
    }

    /// <summary>
    /// The roaming platoons' positions and AI packages (<c>currentPackage</c>, docs/formats/save.md PLATOON) into their PLATOON records.
    /// </summary>
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

    #endregion

    #region Locks and buildings

    /// <summary>
    /// The registered buildings' locks, doors and container contents, and the homes a lock-up round locked, into their zones' state records. A
    /// building the save has no record of is left out, except a level placement (one placed during the session) whose containers hold items: it gets
    /// a state record in its zone's file (<see cref="AddPlacedState"/>), since its level-edit state is rebuilt without them.
    /// </summary>
    static void WriteLocks(SaveGame game, World world, Dictionary<string, FcsRecord> buildings, WorldLevelData? levels)
    {
        var locks = world.Systems.OfType<Locks.LockSystem>().FirstOrDefault();
        Dictionary<string, BuildingPlacement>? placements = null;
        foreach (var b in world.Fixtures.Buildings)
        {
            if (!buildings.TryGetValue(b, out var state))
            {
                if (levels is null || !world.Fixtures.HasContents(b)) continue;

                placements ??= levels.Buildings().GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(),
                    StringComparer.Ordinal);
                if (!placements.TryGetValue(b, out var placement) || AddPlacedState(game, placement) is not { } added) continue;

                buildings[b] = state = added;
            }

            world.Fixtures.WriteBuilding(b, state);
            if (locks is null) continue;

            if (locks.IsHouseLocked(world, b)) state.Bools[Locks.LockSystem.HouseLockedKey] = true;
            else state.Bools.Remove(Locks.LockSystem.HouseLockedKey);
        }
    }

    /// <summary>
    /// Puts a level placement into its zone's file of the save: its instance in the building list and a GAMESTATE_BUILDING of its own, with the keys
    /// the building's fixtures read back (completeness, damage and height). Null when the zone has no file. The state has no handle, as a level
    /// building's state in the level data has none.
    /// </summary>
    static FcsRecord? AddPlacedState(SaveGame game, BuildingPlacement placement)
    {
        if (!game.Zones.TryGetValue(placement.Zone, out var zone)) return null;

        var file = zone.File;
        var list = zone.BuildingList;
        if (list is null)
        {
            list = new FcsRecord { Type = (int)FcsRecordType.InstanceCollection, Id = NextRecordId(file), Name = "0",
                StringId = WorldLevelData.BuildingListId };
            file.Records.Add(list);
        }

        string stateId = placement.StateId ?? placement.InstanceId + "-S23";   // the hex of GAMESTATE_BUILDING (35)
        var state = new FcsRecord { Type = (int)FcsRecordType.GamestateBuilding, Id = NextRecordId(file), Name = "0", StringId = stateId };
        state.Bools["is complete"] = true;
        state.Bools["destroyed"] = placement.DestroyedOverride ?? false;
        state.Floats["world Y pos"] = placement.WorldY ?? placement.Position.Y;
        state.Ints["flags"] = 1;   // bit 0: outside

        // as the level edits write the same placement
        if (placement.InteriorLayout is { Length: > 0 } interior) state.Strings["interior layout name"] = interior;
        if (placement.ExteriorLayout is { Length: > 0 } exterior) state.Strings["exterior layout name"] = exterior;
        file.Records.Add(state);
        list.Instances.RemoveAll(i => i.Id == placement.InstanceId);
        var instance = new FcsInstance
        {
            Id = placement.InstanceId,
            Target = placement.BuildingId,
            Position = placement.Position,
            Rotation = placement.Rotation,
        };
        instance.States.Add(stateId);
        list.Instances.Add(instance);
        return state;
    }

    /// <summary>
    /// Takes the next record id of the save file.
    /// </summary>
    static int NextRecordId(SaveFile file)
    {
        int id = file.Records.Count == 0 ? Math.Max(file.Data.NextId, 0) + 1 : Math.Max(file.Data.NextId, file.Records.Max(r => r.Id)) + 1;
        file.Data.NextId = id;   // later writers allocate from it (BuildingSave, Wild)
        return id;
    }

    #endregion

    #region Uniques and selection

    /// <summary>
    /// The unique-character table into the CAMERA record (docs/formats/save.md, <c>FUN_1409a8660</c>): <c>num usedUniques</c>, then per entry the
    /// CHARACTER id, state, player flag and handle; unclaimed imprisoned entries (the <c>prisoners</c> uniques) are left out, the load derives them
    /// again. The handle is the written character's when it is one of the player's squads, else the one the loaded save gave it, else null (engine:
    /// NPC handles are not tracked back from the writer).
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

    /// <summary>
    /// The selected characters, as the camera's selection handles.
    /// </summary>
    static void WriteSelection(SaveGame game, World world, Dictionary<CharacterId, SaveCharacter> written)
    {
        var camera = game.Camera;
        var record = camera.Record;
        for (int k = 0; record.Ints.ContainsKey($"selected_characters{k}I"); k++)
        {
            foreach (var part in new[] { "TYPE", "C", "CS", "I", "S" })
                record.Ints.Remove($"selected_characters{k}{part}");
        }

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

    #endregion
}
