using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Save;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Saving;

/// <summary>Settings of <see cref="SaveLoader.Load"/>.</summary>
public sealed class SaveLoadOptions
{
    /// <summary>Put the save's relation table into <c>PopulationData.Relations</c> (it is one table shared by the data; a loaded game replaces its start values).</summary>
    public bool ApplyRelations { get; init; } = true;

    /// <summary>
    /// Set the relation table to the FACTION records' start values first (an import without the save's relations; the table is shared by the data, so the last game's values would stay
    /// otherwise), and the factions the player knows to those of the setup (<see cref="PopulationData.KnownAtSetup"/>) instead of the save's.
    /// </summary>
    public bool ResetRelations { get; init; }

    /// <summary>
    /// Make the save's other platoons (not the player's) <see cref="Platoon"/> stand-ins in <see cref="World.Platoons"/>, at their saved positions, when they belong to a placed
    /// town: the population makes their saved characters when a zone near them is active (<see cref="SavedPlatoon"/>; town-bound ones are pinned to their town).
    /// </summary>
    public bool NpcPlatoons { get; init; } = true;

    /// <summary>The look of a spawned character, rolled by this source from the character's saved serial (the saved CHARACTER_APPEARANCE is not read into a drawable look yet). Null: no look.</summary>
    public IAppearanceSource? Appearances { get; init; }
}

/// <summary>
/// Loads a <see cref="SaveGame"/> into a <see cref="World"/> (docs/simulation.md "Saves"): the clock, the factions' state and relations, the player's money and
/// platoons with their characters (positions, stats, medical state), the camera's selection, and the other platoons as stand-ins that make their saved characters when their zone loads. Everything else a save
/// holds (towns, war state, weather (the host restores it), research, zone buildings, items, appearance sliders) stays in <see cref="LoadedSave.Source"/> for <see cref="SaveCapture"/> to carry on.
/// Call it on a world that has not run a tick yet and holds no characters.
/// </summary>
public static class SaveLoader
{
    /// <summary>Meitou-only CAMERA string: the world seed in decimal (docs/formats/save.md "CAMERA"). The original ignores it.</summary>
    public const string SeedKey = "meitou seed";

    public static LoadedSave Load(World world, SaveGame save, PopulationData data, SaveLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(data);
        options ??= new SaveLoadOptions();
        if (world.Tick != 0 || world.Characters.Count != 0) throw new InvalidOperationException("A save is loaded into a world that has not started and has no characters.");
        // The seed a Meitou save was made with (its seeded layout: the nests it did not write roll again where they were); a base save keeps the world's.
        if (save.Camera.Record.Strings.GetValueOrDefault(SeedKey) is { } seedText && ulong.TryParse(seedText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong seed)) world.RestoreSeed(seed);

        var camera = save.Camera;
        var loaded = new LoadedSave { Source = save, Clock = new SaveClock(camera.Day, camera.Hour, camera.Minute) };
        // The CAMERA's mods list names the data files the save was made with (without extension; gamedata.base as "base", docs/formats/save.md).
        var running = new HashSet<string>(data.Db.Files.Select(f => f == "gamedata.base" ? "base" : Path.GetFileNameWithoutExtension(f)), StringComparer.OrdinalIgnoreCase);
        foreach (var mod in camera.Record.References.GetValueOrDefault("mods") ?? [])
            if (!running.Contains(mod.TargetStringId)) loaded.MissingMods.Add(mod.TargetStringId);
        if (loaded.MissingMods.Count > 0) loaded.Notes.Add($"Made with data files this game does not load: {string.Join(", ", loaded.MissingMods)}.");
        if (options.ResetRelations) data.Relations.Reset();
        LoadFactions(save, data, options, loaded);
        loaded.RelationBaseline = Snapshot(data.Relations);
        loaded.RankBaseline = [.. Enumerable.Range(0, data.Relations.Count).Select(data.Relations.Rank)];
        loaded.TrustBaseline = [.. Enumerable.Range(0, data.Relations.Count).Select(data.Relations.GlobalTrust)];
        loaded.WarBaseline = [.. Enumerable.Range(0, data.Relations.Count * data.Relations.Count).Select(i => data.Relations.AtWar(i / data.Relations.Count, i % data.Relations.Count))];

        // The player.
        var playerFaction = save.PlayerFaction;
        int playerIndex = playerFaction is not null && data.FactionIndex(playerFaction.Id) is var pi and >= 0 ? pi : data.PlayerFaction;
        world.Player.Faction = playerIndex;
        world.Player.Money = camera.PlayerMoney;
        // A renamed player faction (the CAMERA's name differs from the data's).
        world.Player.FactionName = camera.PlayerFactionName is { Length: > 0 } savedName && playerIndex >= 0 && savedName != data.Factions[playerIndex].Name ? savedName : null;
        world.Player.SquadAi = SquadAiKeys.Read(camera.Record);
        if (camera.Record.Bools.ContainsKey("ditch") && camera.Record.Ints.TryGetValue(SquadAiKeys.FormationKey, out int formation))
            SquadAiKeys.SetFormation(world, playerIndex, formation, data.Factions);
        // The original's loader reads `bl` only from a save holding the advanced options (float `cod`; docs/game/character-stats.md).
        world.Player.BanditsLoot = !camera.Record.Floats.ContainsKey("cod") || camera.Record.Bools.GetValueOrDefault("bl", true);
        BedSave.Read(camera.Record, world);
        LoadTownKnowledge(save, data, world.Player);
        world.Player.KnownFactions = KnownFactions(playerFaction, data, world.Player, options);
        LoadUniques(camera.Record, data, world.Uniques);

        // Building locks and doors are taken from the zones when their town registers; held characters name their bed or cage by building instance.
        var buildingStates = HeldSave.BuildingStates(save);
        world.Fixtures.Saved = buildingStates;
        world.Fixtures.Items = data.Items;
        var instanceOf = new Dictionary<Meitou.Data.Fcs.FcsRecord, string>(ReferenceEqualityComparer.Instance);
        foreach (var (instance, record) in buildingStates) instanceOf[record] = instance;
        var handles = save.BuildHandles();

        // The research the player has finished, and the buildings the player made (docs/simulation.md "Buildings").
        ResearchSave.Load(save.Research, world.Research, data.Db);
        if (playerFaction is not null) BuildingSave.Read(save, world, data.Db, playerFaction.Id, playerIndex);
        // The machines of the player's complete buildings: recipe slots, storage, farms (docs/simulation.md "Production").
        if (playerFaction is not null && world.Systems.OfType<Production.ProductionSystem>().FirstOrDefault() is { } production) MachineSave.Read(save, world, production, data.Items);
        // The loose items of the zone files (docs/simulation.md "Ground items").
        if (GroundItemSave.Load(save, world, data.Items, id => data.FactionIndex(id) is var f and >= 0 && !data.Factions[f].NotReal ? f : -1) is > 0 and var lost) loaded.Notes.Add($"{lost} loose items name records this game does not have; they stay in the zone files.");

        var races = new Dictionary<string, RaceData?>(StringComparer.Ordinal);
        var parts = new Dictionary<string, BodyPartTemplate?>(StringComparer.Ordinal);
        var bySource = new Dictionary<SaveCharacter, CharacterId>();
        var assembly = new CharacterAssembly(data);
        // The NPC platoons are made when their zones load; what that needs stays with the world (docs/simulation.md "Saves").
        var context = new SavedPopulation
        {
            Handles = handles, InstanceOf = instanceOf, Appearances = options.Appearances, Clock = loaded.Clock, PlayerFaction = playerIndex,
        };
        world.Saved = context;
        context.Memory = world.Systems.OfType<Dialogue.DialogueSystem>().FirstOrDefault()?.Memory;
        ReadNests(save, data, context);
        foreach (var town in save.Towns)
            if (!town.IsNest && town.Instance.Length > 0) context.TownStates.TryAdd(town.Instance, town.Record);
        // The towns' replacements (replacementTown: the TOWN in their place, "" none).
        data.ClearReplacements();
        foreach (var site in data.Sites)
            if (site.InstanceId.Length > 0 && context.TownStates.TryGetValue(site.InstanceId, out var state)
                && state.Strings.GetValueOrDefault("replacementTown") is { Length: > 0 } replacement && data.Db.Find(replacement) is { Type: FcsRecordType.TOWN } record)
                data.SetReplacement(site.Index, TownData.From(record));
        // The war states, and the town handles their targets and homes name (docs/formats/save.md WAR_SAVESTATE).
        var siteOf = data.Sites.Where(s => s.InstanceId.Length > 0).GroupBy(s => s.InstanceId).ToDictionary(g => g.Key, g => g.First().Index, StringComparer.Ordinal);
        foreach (var town in save.Towns)
            if (!town.IsNest && siteOf.TryGetValue(town.Instance, out int site) && !town.Handle.IsNull) context.TownSites.TryAdd((town.Handle.C, town.Handle.CS), site);
        foreach (var f in save.Factions)
        {
            if (f.War is { } war) context.Wars.TryAdd(f.Id, war);
            context.Prosperity.TryAdd(f.Id, f.Prosperity);
        }
        var campaignHomes = WarSave.PlatoonHomes(context.Wars.Values);
        foreach (var platoon in save.Platoons)
        {
            bool mine = playerFaction is not null && platoon.FactionId == playerFaction.Id;
            if (mine && platoon.IsLoaded)
                LoadPlayerPlatoon(world, platoon, data, options, loaded, playerIndex, races, parts, bySource, assembly, context);
            else if (!mine && options.NpcPlatoons)
                LoadNpcPlatoon(world, platoon, data, loaded, context, campaignHomes.TryGetValue((platoon.Handle.C, platoon.Handle.CS), out var home) ? home : null);
        }
        if (loaded.PlayerPlatoons.Count > 0) world.Player.Squad = loaded.PlayerPlatoons[0].Squad.Id;
        // Stolen items name their owners once those are made (the rest as NPC platoons are).
        context.ResolveOwners(world);
        // What the player's squads remembered (of the characters made so far; an NPC not made yet is not named again).
        if (context.Memory is { } memory)
            foreach (var lp in loaded.PlayerPlatoons) memory.Restore(lp.Squad.Id, MemorySave.Read(lp.Platoon.Record, context.CharacterOf));

        // The selection.
        var selected = new List<Hand> { camera.SelectedCharacter };
        for (int k = 0; camera.Record.Ints.ContainsKey($"selected_characters{k}I"); k++) selected.Add(Hand.Read(camera.Record, $"selected_characters{k}"));
        foreach (var h in selected)
            if (!h.IsNull && handles.Character(h) is { } c && bySource.TryGetValue(c, out var id) && !world.Player.Selection.Contains(id))
                world.Player.Selection.Add(id);
        // The squad bar shows the selected character's squad, else the first in order with members.
        if (world.Player.Selection.Count > 0 && world.Characters.Cold(world.Player.Selection[0].Slot) is { } first && world.Squads.Find(first.SquadId) is { PlatoonId: < 0 } shown
            && shown.Faction == playerIndex)
            world.Player.Squad = shown.Id;
        else if (SquadManagement.PlayerSquads(world) is { Count: > 0 } squads)
            world.Player.Squad = (squads.FirstOrDefault(s => s.Members.Count > 0) ?? squads[0]).Id;
        return loaded;
    }

    internal static float[] Snapshot(FactionRelations relations)
    {
        int n = relations.Count;
        var all = new float[n * n];
        for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++) all[a * n + b] = relations.Get(a, b);
        return all;
    }

    /// <summary>
    /// The unique-character table of the CAMERA record (docs/formats/save.md, <c>FUN_1409aa270</c>): each entry claimed with its state, player flag and handle; one whose
    /// CHARACTER id does not resolve is dropped. Live handles are bound when the characters are made.
    /// </summary>
    static void LoadUniques(Meitou.Data.Fcs.FcsRecord camera, PopulationData data, UniqueCharacters uniques)
    {
        int n = camera.Ints.GetValueOrDefault("num usedUniques");
        for (int k = 0; k < n; k++)
        {
            if (camera.Strings.GetValueOrDefault($"usedUniques{k}") is not { } id || data.Db.Find(id) is null) continue;
            int state = camera.Ints.GetValueOrDefault($"usedUniquesState{k}", 1);
            uniques.Load(id, state is >= 0 and <= 2 ? (UniqueState)state : UniqueState.Alive, camera.Bools.GetValueOrDefault($"usedUniquesPlayer{k}"), Hand.Read(camera, $"usedUniques{k}handle"));
        }
    }

    /// <summary>
    /// The factions met (docs/game/factions-squads-towns.md "Factions met"): the save's <c>known</c> list; those of the setup when the save has no player faction or an import leaves
    /// the relations out. An empty list (older Meitou saves wrote one; the original's always holds the setup's) takes the setup's and the owners of the towns and camps explored
    /// (engine choice). After the town knowledge is loaded.
    /// </summary>
    static HashSet<int> KnownFactions(SaveFaction? playerFaction, PopulationData data, PlayerState player, SaveLoadOptions options)
    {
        if (playerFaction is null || options.ResetRelations) return data.KnownAtSetup();
        if (playerFaction.Known.Count > 0) return [.. playerFaction.Known.Select(data.FactionIndex).Where(f => f >= 0)];
        var known = data.KnownAtSetup();
        for (int s = 0; s < player.Towns.Length && s < data.SiteFactions.Count; s++)
            if ((player.Towns[s] & TownKnowledge.Explored) != 0 && data.SiteFactions[s] >= 0) known.Add(data.SiteFactions[s]);
        foreach (var camp in player.Camps)
            if ((camp.Known & TownKnowledge.Explored) != 0 && data.FactionIndex(camp.FactionId) is var f and >= 0) known.Add(f);
        return known;
    }

    /// <summary>The towns' <c>discovered</c>, <c>explored</c> and <c>recently discovered</c> bools, matched to the placed towns by instance id.</summary>
    static void LoadTownKnowledge(SaveGame save, PopulationData data, PlayerState player)
    {
        var sites = data.Sites.Where(s => s.InstanceId.Length > 0).ToDictionary(s => s.InstanceId, s => s.Index, StringComparer.Ordinal);
        foreach (var town in save.Towns)
        {
            var flags = (town.Discovered ? TownKnowledge.Discovered : 0) | (town.Record.Bools.GetValueOrDefault("explored") ? TownKnowledge.Explored : 0)
                | (town.Record.Bools.GetValueOrDefault("recently discovered") ? TownKnowledge.Recent : 0);
            if (flags == 0) continue;
            if (sites.TryGetValue(town.Instance, out int site)) player.Know(site, flags);
            // A camp Meitou saved: its wild site and TOWN record; the position comes when the nest loads.
            else if (town.IsNest && town.Instance.StartsWith(SaveCapture.CampInstancePrefix, StringComparison.Ordinal)
                && town.Instance[SaveCapture.CampInstancePrefix.Length..].Split(':', 2) is [var index, var townId]
                && int.TryParse(index, System.Globalization.CultureInfo.InvariantCulture, out int wild) && data.Db.Find(townId) is { } record)
            {
                var nest = TownData.From(record);
                int faction = nest.Faction is { } id ? data.FactionIndex(id) : -1;
                player.KnowCamp(new CampView(wild, townId, nest.Name, nest.Faction ?? "", faction >= 0 && data.Factions[faction].NotReal, null, flags), flags);
            }
        }
    }

    static void LoadFactions(SaveGame save, PopulationData data, SaveLoadOptions options, LoadedSave loaded)
    {
        foreach (var f in save.Factions)
        {
            var state = new FactionState { Id = f.Id, Name = f.Name, Prosperity = f.Prosperity, PlatoonCounter = f.PlatoonCounter, IsPlayer = f.IsPlayer };
            foreach (var r in f.Relations) state.Relations[r.FactionId] = r;
            loaded.Factions.Add(state);
            int a = data.FactionIndex(f.Id);
            if (!options.ApplyRelations || a < 0) continue;
            data.Relations.SetRank(a, f.Rank);
            data.Relations.SetGlobalTrust(a, f.GlobalTrust);
            foreach (var r in f.Relations)
                if (data.FactionIndex(r.FactionId) is var b and >= 0)
                {
                    data.Relations.Set(a, b, r.Relation);
                    data.Relations.SetWar(a, b, r.War);
                }
        }
        foreach (var f in save.Factions)
            if (data.FactionIndex(f.Id) < 0) loaded.Notes.Add($"Faction {f.Id} ({f.Name}) is not in the game data; its state is kept in the save only.");
    }

    static void LoadPlayerPlatoon(World world, SavePlatoon platoon, PopulationData data, SaveLoadOptions options, LoadedSave loaded,
        int playerIndex, Dictionary<string, RaceData?> races, Dictionary<string, BodyPartTemplate?> parts, Dictionary<SaveCharacter, CharacterId> bySource, CharacterAssembly assembly,
        SavedPopulation context)
    {
        var squad = new Squad
        {
            Id = world.Squads.NextId(),
            TemplateId = platoon.SquadTemplate,
            // The record's name is what the squad tabs show (a renamed squad's, the original's `platoon->+0x78->+0x18`, Observed); its string id names the file.
            Name = platoon.Record.Name is { Length: > 0 } shown ? shown : platoon.Name,
            Index = platoon.Record.Ints.GetValueOrDefault("squad index"),
            SaveName = platoon.Name,
            // The faction's squad of the dead, found again by its name (FUN_1407f3fb0); its dead are not made, so it stays empty and hidden.
            Dead = platoon.Name == SquadManagement.DeadSquadName || platoon.Record.Name == SquadManagement.DeadSquadName,
            Faction = playerIndex,
            Position = platoon.Position,
            HomeCentre = new Vector2(platoon.Position.X, platoon.Position.Z),
            HomeRadius = 100,
        };
        foreach (var sc in platoon.Characters)
        {
            if (SavedCharacters.Read(sc, platoon.Name, data, options.Appearances, races, parts, loaded.Notes.Add) is not { } p) continue;
            if (p.Medical is { Dead: true })
            {
                loaded.Notes.Add($"{platoon.Name}: {sc.Name} is dead in the save; not made, and kept as it is when saving.");
                continue;
            }
            var id = SavedCharacters.Make(world, p, platoon.Name, squad, playerIndex, playerIndex, true, sc.Position, SaveRotation.YawOf(sc.Rotation), CharacterTask.Idle, SpeedMode.Free,
                data, assembly, context, null, loaded.Notes);
            squad.Members.Add(id);
            bySource[sc] = id;
            context.Spawned[(sc.Handle.C, sc.Handle.I)] = id;
            loaded.Characters.Add(id);
            loaded.SpawnedSlots.Add((platoon.Name, sc.Handle.I));
            if (sc.IsLeader && squad.Leader.IsNone) squad.Leader = id;
        }
        if (squad.Leader.IsNone && squad.Members.Count > 0) squad.Leader = squad.Members[0];
        world.Squads.Add(squad);
        loaded.PlayerPlatoons.Add(new LoadedPlatoon(platoon, squad));
    }

    /// <summary>
    /// An NPC platoon of a placed town: a <see cref="Platoon"/> stand-in at its saved place carrying its saved characters (<see cref="SavedPlatoon"/>), made when a zone
    /// near it is active. Town-bound platoons (<see cref="Travels"/> false) are pinned to their town and are its whole population (it makes none of its own); the others travel.
    /// A platoon a war state names (<paramref name="campaignHome"/>: its campaign's home town) always travels: its campaign takes it on when the war managers read their states.
    /// One with no placed base town is homed at its campaign's home town, never made a wild platoon, which would leave its campaign without it.
    /// </summary>
    static void LoadNpcPlatoon(World world, SavePlatoon platoon, PopulationData data, LoadedSave loaded, SavedPopulation context, Hand? campaignHome = null)
    {
        bool campaign = campaignHome is not null;
        string baseTown = platoon.Record.Strings.GetValueOrDefault("basetown", "");
        int origin = -1;
        for (int i = 0; i < data.Sites.Count && baseTown.Length > 0; i++)
            if (data.Sites[i].Town.Id == baseTown) { origin = i; break; }
        if (origin < 0 && campaignHome is { IsNull: false } home && context.TownSites.TryGetValue((home.C, home.CS), out int homeSite)) origin = homeSite;
        if (origin < 0 && platoon.SquadTemplate.Length > 0 && LoadWildPlatoon(world, platoon, data, loaded, context)) return;
        if (origin < 0 || platoon.SquadTemplate.Length == 0)
        {
            loaded.Notes.Add($"{platoon.Name}: no placed town for its base town '{baseTown}' or no squad template; its platoon stays in the save only.");
            return;
        }
        // Its AI package: the saved currentPackage when the template lists it, else the slot rule's; the package's unloaded func is its off-screen job.
        string? saved = platoon.Record.Strings.TryGetValue("currentPackage", out var current) && current.Length > 0 ? current : null;   // a string: the AI_PACKAGE id (docs/formats/save.md)
        var (package, job) = data.Db.Find(platoon.SquadTemplate) is { } templateRecord && SquadFactory.IsTemplate(templateRecord)
            ? Ai.PlatoonJobs.For(data.Ai, Meitou.Data.Gameplay.SquadTemplate.From(templateRecord), saved) : (null, Ai.PlatoonJob.None);
        // Town-bound platoons are the town's own: its residents, bar squads, slaves and farm animals. They stay home, and the town makes none of its own.
        int siteFaction = data.SiteFactions.Count > origin ? data.SiteFactions[origin] : -1;
        bool travels = campaign || Travels(platoon, job, data.Sites[origin].Town, siteFaction >= 0 ? data.Factions[siteFaction] : null);
        int faction = data.FactionIndex(platoon.FactionId);
        // A platoon without characters (no file, or none in it) has nothing to make: a town's stays in the save as it is, a travelling one is a stand-in whose members the
        // template makes, as for the population's own.
        bool hasCharacters = platoon.IsLoaded && platoon.Characters.Count > 0;
        bool unmade = !hasCharacters && !travels && Unmade(platoon);
        if (!hasCharacters && !travels && !unmade) return;
        SavedPlatoon? sp = null;
        if (unmade) sp = new SavedPlatoon { Source = platoon, Pinned = true, Anchor = new Vector2(platoon.Position.X, platoon.Position.Z) };
        if (hasCharacters)
        {
            sp = new SavedPlatoon { Source = platoon, Pinned = !travels, Anchor = new Vector2(platoon.Position.X, platoon.Position.Z) };
            foreach (var sc in platoon.Characters) sp.Members.Add(new SavedMember { Source = sc });
        }
        ulong key = Rng.Mix(Rng.StableHash(platoon.Name));
        int size = Math.Max(platoon.CharCount, platoon.Characters.Count);
        // A campaign's stand-in with no characters is made from its template: the plan gives its size (the men its campaign counts).
        if (size == 0 && campaign && data.Db.Find(platoon.SquadTemplate) is { } sizeRecord && SquadFactory.IsTemplate(sizeRecord))
            size = SquadFactory.Plan(data.Db, Meitou.Data.Gameplay.SquadTemplate.From(sizeRecord), 1, world.Seed, key, data.States).TotalMembers;
        var p = new Platoon
        {
            Id = unmade ? world.Platoons.NextReservedId() : world.Platoons.NextId(),
            Origin = origin,
            TemplateId = platoon.SquadTemplate,
            Faction = faction >= 0 ? faction : siteFaction,
            Key = key,
            Size = size,
            Position = new Vector2(platoon.Position.X, platoon.Position.Z),
            Money = platoon.Money,
            State = PlatoonState.Unloaded,
            Package = package?.Id, Job = job,
            Saved = sp,
            Unmade = unmade,
            Imprisoned = platoon.Record.Bools.GetValueOrDefault("imprisoned"),
            Dead = platoon.Record.Bools.GetValueOrDefault("dead"),
            IntactFlag = platoon.Record.Bools.GetValueOrDefault("intact", true),
        };
        world.Platoons.Add(p);
        if (!platoon.Handle.IsNull) context.PlatoonIds.TryAdd((platoon.Handle.C, platoon.Handle.CS), p.Id);
        if (sp is not null)
        {
            sp.PlatoonId = p.Id;
            context.Platoons.Add(sp);
            context.ByName[sp.Name] = sp;
        }
        if (unmade) loaded.Notes.Add($"{platoon.Name}: never made by the game; made from its template when it activates.");
        if (travels) loaded.RoamingPlatoons[p.Id] = platoon.Name;
        else loaded.TownBoundPlatoons++;
    }

    /// <summary>
    /// Whether a saved town platoon with no file is one the game never made: the original builds a <c>never been activated</c> platoon from its template on activation
    /// (<c>FUN_1404ffad0</c>, <b>Verified</b>); the <c>char count</c> &gt; 0 and BUILDING <c>homebuilding</c> filter is Meitou's own (docs/formats/save.md "NPC platoons").
    /// It becomes a stand-in (<see cref="Platoon.Unmade"/>) instead of staying in the save.
    /// </summary>
    internal static bool Unmade(SavePlatoon platoon) =>
        !platoon.IsLoaded && platoon.Record.Bools.GetValueOrDefault("never been activated") && platoon.CharCount > 0
        && Hand.Read(platoon.Record, "homebuilding") is { IsNull: false, Type: (int)FcsRecordType.BUILDING };

    /// <summary>
    /// Whether a saved NPC platoon travels and so gets a roaming stand-in (<b>engine rule</b>, from the five saves of docs/formats/save.md: residents are marked <c>is resident</c>, and
    /// the bar squads, slaves and farm animals of a town have a package whose unloaded func keeps them home, 4, or 0): not a resident, and an off-screen job that moves it
    /// (unloaded func 2, 5 or 6) and not a bar squad of the town or its faction (which the population makes with the town), or a template of its base town's <c>roaming squads</c>.
    /// </summary>
    internal static bool Travels(SavePlatoon platoon, Ai.PlatoonJob job, TownData baseTown, FactionData? townFaction)
    {
        string template = platoon.SquadTemplate;
        if (platoon.Record.Bools.GetValueOrDefault("is resident")) return false;
        if (baseTown.RoamingSquads.Any(r => r.Id == template)) return true;
        if (baseTown.BarSquads.Any(r => r.Id == template) || townFaction?.BarSquads.Any(r => r.Id == template) == true) return false;
        return job is Ai.PlatoonJob.PatrolShortRange or Ai.PlatoonJob.TravelTarget or Ai.PlatoonJob.TravelTargetFast;
    }

    /// <summary>
    /// The save's nests (docs/formats/save.md "Nests"): the entries of <c>quick.save</c>'s town list whose GAMESTATE_TOWN is a nest (<c>is nest</c>, a type-85 <c>hand</c>) and
    /// whose target is a TOWN of the game data, in list order.
    /// </summary>
    static void ReadNests(SaveGame save, PopulationData data, SavedPopulation context)
    {
        if (save.TownInstanceList is not { } list) return;
        var byId = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        foreach (var r in save.Quick.Records) byId.TryAdd(r.StringId, r);
        foreach (var entry in list.Instances)
        {
            if (entry.IsRemoved || entry.States.Count == 0 || !byId.TryGetValue(entry.States[0], out var state)) continue;
            if (state.RecordType != FcsRecordType.GAMESTATE_TOWN || !state.Bools.GetValueOrDefault("is nest")) continue;
            var hand = Hand.Read(state, "hand");
            if (hand.Type != (int)FcsRecordType.NEST || data.Db.Find(entry.Target) is not { Type: FcsRecordType.TOWN }) continue;
            context.Nests.Add(new SavedNest(entry.Id, entry.Target, entry.Position, hand, state));
        }
    }

    /// <summary>
    /// A wild platoon (docs/formats/save.md "Wild and nest platoons"): one whose <c>hometown</c> names a nest of the save is that nest's, one with no home town and no placed
    /// <c>basetown</c> is homeless. Either becomes a stand-in at its saved place that never travels; the population binds it to its nest or its sector, which make that much
    /// less of their own. False when it is neither (the caller notes it).
    /// </summary>
    static bool LoadWildPlatoon(World world, SavePlatoon platoon, PopulationData data, LoadedSave loaded, SavedPopulation context)
    {
        var home = Hand.Read(platoon.Record, "hometown");
        SavedNest? nest = null;
        if (home.Type == (int)FcsRecordType.NEST)
            foreach (var n in context.Nests)
                if (n.Handle.C == home.C && n.Handle.CS == home.CS) { nest = n; break; }
        if (nest is null && !home.IsNull) return false;
        if (data.Db.Find(platoon.SquadTemplate) is not { } templateRecord || !SquadFactory.IsTemplate(templateRecord)) return false;
        if (!platoon.IsLoaded || platoon.Characters.Count == 0) return true;   // nothing to make: it stays in the save as it is
        var kind = nest is null ? WildKind.Sector : WildKind.Nest;
        var sp = new SavedPlatoon { Source = platoon, Pinned = false, Wild = kind, Nest = nest, Anchor = new Vector2(platoon.Position.X, platoon.Position.Z) };
        foreach (var sc in platoon.Characters) sp.Members.Add(new SavedMember { Source = sc });
        string? saved = platoon.Record.Strings.TryGetValue("currentPackage", out var current) && current.Length > 0 ? current : null;
        var (package, job) = Ai.PlatoonJobs.For(data.Ai, Meitou.Data.Gameplay.SquadTemplate.From(templateRecord), saved);
        int faction = data.FactionIndex(platoon.FactionId);
        var p = new Platoon
        {
            Id = world.Platoons.NextId(),
            Origin = -1,
            TemplateId = platoon.SquadTemplate,
            Faction = faction,
            Key = Rng.Mix(Rng.StableHash(platoon.Name)),
            Size = Math.Max(platoon.CharCount, platoon.Characters.Count),
            Position = new Vector2(platoon.Position.X, platoon.Position.Z),
            Money = platoon.Money,
            State = PlatoonState.Unloaded,
            Package = package?.Id, Job = job,
            Saved = sp,
            Imprisoned = platoon.Record.Bools.GetValueOrDefault("imprisoned"),
            Dead = platoon.Record.Bools.GetValueOrDefault("dead"),
            IntactFlag = platoon.Record.Bools.GetValueOrDefault("intact", true),
        };
        world.Platoons.Add(p);
        sp.PlatoonId = p.Id;
        context.Platoons.Add(sp);
        context.ByName[sp.Name] = sp;
        loaded.WildPlatoons++;
        return true;
    }
}
