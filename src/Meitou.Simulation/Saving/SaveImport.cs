using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Saving;

/// <summary>The ticks of the import window, as the bits <c>SaveManager::importGame</c> (<c>FUN_140378a30</c>) tests in its flags (docs/formats/save.md "Importing a game"). Bit 0x2 is always added by the window and tested by nothing.</summary>
[Flags]
public enum ImportOptions
{
    None = 0,
    /// <summary>"Reset squad positions": the squads start at <see cref="SaveImport.ResetPosition"/>.</summary>
    ResetPositions = 0x1,
    /// <summary>"Import Buildings": the player's buildings (zone files) and the player's towns.</summary>
    Buildings = 0x4,
    /// <summary>"Import Research": the finished and queued research.</summary>
    Research = 0x8,
    /// <summary>"Import Dead NPCs": the unique characters that are dead or imprisoned stay so.</summary>
    DeadNpcs = 0x10,
    /// <summary>"Import Relations": the factions' relation tables.</summary>
    Relations = 0x20,
    /// <summary>What the window has ticked at first (everything but the reset).</summary>
    Default = Buildings | Research | DeadNpcs | Relations,
}

/// <summary>
/// Importing a game (docs/formats/save.md "Importing a game", <c>FUN_140378a30</c>): a new world that keeps only what the player brought from an old save. <see cref="Filter"/> cuts a
/// loaded <see cref="SaveGame"/> down to that part, in the shape the loader and the capture already work with; <see cref="SaveLoader"/> then makes the world from it as from any save,
/// so the characters, their items, medical state and stats are the old platoon files' unchanged. The original does the same work with its own containers: the save is read into the
/// save container, the world is reset, and each part is copied across.
/// </summary>
public static class SaveImport
{
    /// <summary>Where "Reset squad positions" puts the player's squads (<c>FUN_140378a30</c>, the camera and <c>FUN_14036b370</c>); squad i of the list stands at +(i, 0, i).</summary>
    public static readonly Vector3 ResetPosition = new(-43500, 0, -6625);

    /// <summary>The keys of a non-player town's state that an import keeps: what the player knew of it (docs/formats/save.md "Towns").</summary>
    static readonly string[] KnowledgeBools = ["is nest", "discovered", "explored", "recently discovered"];

    /// <summary>
    /// The part of <paramref name="game"/> an import with <paramref name="options"/> keeps, as a new save (<paramref name="game"/> itself is cut down by it and not to be used afterwards): the player's squads with their platoon files,
    /// the clock, money, camera and squad settings, the towns' discovery, and by the ticks the player's building and town records, research, the dead and imprisoned uniques and the
    /// factions' relations. Everything else (other factions' platoons, nests, the weather, the war states, loose items, others' buildings) is left out, and the world makes it new.
    /// </summary>
    public static SaveGame Filter(SaveGame game, ImportOptions options, PopulationData data)
    {
        // Cut down in place; the views of a SaveGame (research, weather...) are only rebuilt by making a new one from the records, at the end.
        var player = game.PlayerFaction ?? throw new InvalidOperationException("The save has no player faction.");
        options = AllowedFor(options, game.Camera.Version);
        string playerId = player.Id;
        var records = game.Quick.Records;

        // The player's squads, in the order of their squad index, renumbered from 0 (the original erases the index and the list order gives it).
        var keep = game.Platoons.Where(p => p.FactionId == playerId && p.IsLoaded && p.Name != SquadManagement.DeadSquadName && p.Record.Name != SquadManagement.DeadSquadName)
            .OrderBy(p => p.Record.Ints.GetValueOrDefault("squad index")).ToList();
        // Engine choice: the original salvages a squad from a "Nameless_0.platoon" file and pays 10000 cats; here a save without a player squad is refused.
        if (keep.Count == 0) throw new InvalidOperationException("The save has no squad of the player's to import.");
        records.RemoveAll(r => r.RecordType == FcsRecordType.Platoon && !keep.Any(p => ReferenceEquals(p.Record, r)));
        for (int i = 0; i < keep.Count; i++)
        {
            var platoon = keep[i];
            platoon.Record.Ints["squad index"] = i;
            if ((options & ImportOptions.ResetPositions) == 0) continue;
            var at = ResetPosition + new Vector3(i, 0, i);
            platoon.Position = at;
            // The members stand with their squad (engine choice: the original moves the squad and the members follow on activation; here each stands 2 units from the last).
            for (int m = 0; m < platoon.Characters.Count; m++) platoon.Characters[m].Position = at + new Vector3(2 * m, 0, 0);
        }

        FilterUniques(game, keep, options, data);
        FilterFactions(game, options);
        FilterTowns(game, playerId, options, data);
        AddArtifacts(game, data);
        FilterZones(game, playerId, options);

        // The weather and the terrain decals are the new world's; the research is the start's unless it is imported.
        records.RemoveAll(r => r.RecordType is FcsRecordType.Biomes or FcsRecordType.TerrainDecals or FcsRecordType.WarSavestate);
        if ((options & ImportOptions.Research) == 0 && records.FirstOrDefault(r => r.RecordType == FcsRecordType.Research) is { } research)
        {
            foreach (var key in research.Strings.Keys.Where(k => k.StartsWith("finished", StringComparison.Ordinal) || k.StartsWith("current", StringComparison.Ordinal) || k.StartsWith("paid", StringComparison.Ordinal)).ToList()) research.Strings.Remove(key);
            foreach (var key in research.Floats.Keys.Where(k => k.StartsWith("current prog", StringComparison.Ordinal)).ToList()) research.Floats.Remove(key);
            research.Strings["finished0"] = Data.Gameplay.Construction.ResearchGate.DefaultStartId;
            research.Floats["num finished"] = 1;
            research.Floats["num currents"] = 0;
        }
        game.Camera.PlayerMoney = Math.Max(0, game.Camera.PlayerMoney);
        // The same records and files under views made again from them.
        var imported = new SaveGame(game.Quick) { Portraits = game.Portraits };
        foreach (var p in imported.Platoons) p.File = game.Platoons.First(q => ReferenceEquals(q.Record, p.Record)).File;
        foreach (var (coordinate, zone) in game.Zones) imported.Zones[coordinate] = zone;
        imported.Reindex();
        imported.UpdateDerived();
        return imported;
    }

    /// <summary>The oldest save version whose reset, buildings, research and dead-NPC ticks the import window leaves live (<c>ImportGameMenu::vfunc_1</c> <c>FUN_14047cee0</c>).</summary>
    public const int TicksFromVersion = 0 << 24 | 90 << 12 | 0;

    /// <summary>
    /// A save version as the import window compares it (<c>FUN_14047acf0</c>, <b>Verified</b> by decompile): "a.b.c" read as three integers, packed a &lt;&lt; 24 | b &lt;&lt; 12 | c; anything else
    /// (an empty or "Ancient" version) is 0.
    /// </summary>
    public static int PackedVersion(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 3 && int.TryParse(parts[0], out int a) && int.TryParse(parts[1], out int b) && int.TryParse(parts[2], out int c) ? (a << 12 | b) << 12 | c : 0;
    }

    /// <summary>
    /// The ticks that apply to a save of <paramref name="version"/>: for one older than 0.90.0 the window disables and unticks Reset squad positions, Buildings, Research and Dead NPCs
    /// (its row selection, <c>FUN_14047cee0</c>, <b>Verified</b> by disassembly), leaving Relations. Meitou applies the same mask here.
    /// </summary>
    public static ImportOptions AllowedFor(ImportOptions options, string version) =>
        PackedVersion(version) < TicksFromVersion ? options & ~(ImportOptions.ResetPositions | ImportOptions.Buildings | ImportOptions.Research | ImportOptions.DeadNpcs) : options;

    /// <summary>Puts the squads of a save <see cref="Filter"/> moved to <see cref="ResetPosition"/> (and their members) on the ground there: the original's position has no height.</summary>
    public static void PlaceOnGround(SaveGame game, Func<float, float, float> groundAt)
    {
        foreach (var platoon in game.Platoons)
        {
            platoon.Position = platoon.Position with { Y = groundAt(platoon.Position.X, platoon.Position.Z) };
            foreach (var c in platoon.Characters) c.Position = c.Position with { Y = groundAt(c.Position.X, c.Position.Z) };
        }
    }

    /// <summary>
    /// The unique-character table (<c>FUN_1409aa270</c>): with "Dead NPCs" the entries of dead uniques stay; imprisoned ones stay only when they are among the player's squads (the
    /// original unclaims the others, whose characters this world does not have). Every unique in a kept squad is entered, alive, with its character's own handle.
    /// </summary>
    static void FilterUniques(SaveGame game, List<SavePlatoon> keep, ImportOptions options, PopulationData data)
    {
        var record = game.Camera.Record;
        var inSquads = new Dictionary<string, Hand>(StringComparer.Ordinal);
        foreach (var platoon in keep)
            foreach (var c in platoon.Characters)
                if (data.Db.Find(c.RecordId) is { } r && UniqueCharacters.IsUnique(r)) inSquads.TryAdd(c.RecordId, c.Handle);

        var table = new List<(string Id, int State, bool Player, Hand Handle)>();
        int n = record.Ints.GetValueOrDefault("num usedUniques");
        for (int k = 0; k < n; k++)
        {
            if (record.Strings.GetValueOrDefault($"usedUniques{k}") is not { } id) continue;
            int state = record.Ints.GetValueOrDefault($"usedUniquesState{k}", 1);
            bool player = record.Bools.GetValueOrDefault($"usedUniquesPlayer{k}");
            var handle = Hand.Read(record, $"usedUniques{k}handle");
            if (inSquads.ContainsKey(id)) { table.Add((id, state, player, inSquads[id])); inSquads.Remove(id); }
            else if (state == 0 && (options & ImportOptions.DeadNpcs) != 0) table.Add((id, 0, player, handle));
        }
        foreach (var (id, handle) in inSquads) table.Add((id, 1, false, handle));

        for (int k = 0; k < n || record.Strings.ContainsKey($"usedUniques{k}"); k++)
        {
            record.Strings.Remove($"usedUniques{k}");
            record.Ints.Remove($"usedUniquesState{k}");
            record.Bools.Remove($"usedUniquesPlayer{k}");
            foreach (var part in new[] { "TYPE", "C", "CS", "I", "S" }) record.Ints.Remove($"usedUniques{k}handle{part}");
        }
        record.Ints["num usedUniques"] = table.Count;
        for (int k = 0; k < table.Count; k++)
        {
            record.Strings[$"usedUniques{k}"] = table[k].Id;
            record.Ints[$"usedUniquesState{k}"] = table[k].State;
            record.Bools[$"usedUniquesPlayer{k}"] = table[k].Player;
            table[k].Handle.Write(record, $"usedUniques{k}handle");
        }
    }

    /// <summary>The factions' state: with "Relations" the relation tables, ranks and the factions met stay, without them the factions start from their FACTION records. War states never stay (their campaigns name platoons this world lacks).</summary>
    static void FilterFactions(SaveGame game, ImportOptions options)
    {
        if ((options & ImportOptions.Relations) != 0) return;
        foreach (var f in game.Factions)
        {
            f.SetRelations([]);
            f.Rank = 0;
        }
        if (game.PlayerFaction is { } player) player.Known = [];
    }

    /// <summary>
    /// The towns (<c>FUN_1403722d0</c>, <c>FUN_140372560</c>): nests go. With "Buildings" the non-nest towns of the player faction that were discovered stay whole (the outposts the player made are in
    /// the town instance list); every other town keeps what the player knew of it. "Dead NPCs" also keeps the town that replaces a destroyed one.
    /// </summary>
    static void FilterTowns(SaveGame game, string playerId, ImportOptions options, PopulationData data)
    {
        var records = game.Quick.Records;
        var dropped = new List<FcsRecord>();
        // The towns the player made (outposts) are the instance list's; the level data's are placed anyway.
        var made = game.TownInstanceList?.Instances.Where(i => i.States.Count > 0).Select(i => i.States[0]).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var town in game.Towns)
        {
            var r = town.Record;
            bool mine = town.FactionId == playerId;
            if (town.IsNest) { dropped.Add(r); continue; }
            if (mine && made.Contains(r.StringId))
            {
                if ((options & ImportOptions.Buildings) != 0 && town.Discovered) { r.Bools["explored"] = true; continue; }
                dropped.Add(r);
                continue;
            }
            if (mine) { r.Bools["discovered"] = true; r.Bools["explored"] = true; }
            if (mine && (options & ImportOptions.Buildings) != 0 && town.Discovered) continue;   // a town of the player's faction is loaded whole
            foreach (var key in r.Bools.Keys.Where(k => !KnowledgeBools.Contains(k)).ToList()) r.Bools.Remove(key);
            foreach (var key in r.Floats.Keys.ToList()) r.Floats.Remove(key);
            foreach (var key in r.Ints.Keys.Where(k => !k.StartsWith("hand", StringComparison.Ordinal) && !k.StartsWith("zz", StringComparison.Ordinal)).ToList()) r.Ints.Remove(key);
            foreach (var key in r.Strings.Keys.Where(k => k is not ("instance" or "faction") && !(k == "replacementTown" && (options & ImportOptions.DeadNpcs) != 0)).ToList()) r.Strings.Remove(key);
            foreach (var key in r.References.Keys.ToList()) r.References.Remove(key);
            foreach (var key in r.Vector3S.Keys.ToList()) r.Vector3S.Remove(key);
            foreach (var key in r.Vector4S.Keys.ToList()) r.Vector4S.Remove(key);
            r.Filenames.Clear();
            r.Instances.Clear();
        }
        dropped.AddRange(TooCloseTowns(game, playerId, dropped, data));
        records.RemoveAll(r => dropped.Contains(r));
        if (game.TownInstanceList is { } list)
        {
            var gone = dropped.Select(r => r.StringId).ToHashSet(StringComparer.Ordinal);
            list.Instances.RemoveAll(i => i.States.Count > 0 && gone.Contains(i.States[0]));
        }
    }

    /// <summary>
    /// "Town too close. Removing" (<c>FUN_14092ae80</c>, <b>Verified</b>): in list order, a town of the player's whose centre lies inside the radius of the nearest other player town that is not a nest marker
    /// is removed, and is no longer a neighbour for the towns after it, so of two overlapping towns the earlier one goes.
    /// </summary>
    static List<FcsRecord> TooCloseTowns(SaveGame game, string playerId, List<FcsRecord> dropped, PopulationData data)
    {
        var sites = data.Sites.Where(s => s.InstanceId.Length > 0).GroupBy(s => s.InstanceId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var mine = game.Towns.Where(t => !t.IsNest && t.FactionId == playerId && !dropped.Contains(t.Record) && sites.ContainsKey(t.Instance)).Select(t => (t.Record, Site: sites[t.Instance])).ToList();
        var removed = new List<FcsRecord>();
        foreach (var (record, site) in mine.ToList())
        {
            var near = mine.Where(o => o.Record != record).OrderBy(o => Vector2.DistanceSquared(Flat(o.Site.Position), Flat(site.Position))).Cast<(FcsRecord Record, TownSite Site)?>().FirstOrDefault();
            if (near is not { } other) continue;
            float radius = other.Site.Town.Radius;
            if (Vector2.DistanceSquared(Flat(other.Site.Position), Flat(site.Position)) >= radius * radius) continue;
            removed.Add(record);
            mine.RemoveAll(m => m.Record == record);
        }
        return removed;
    }

    static Vector2 Flat(Vector3 v) => new(v.X, v.Z);

    /// <summary>
    /// The towns get their artifacts as in a new game (docs/game/economy.md "Artifacts"): a town of the player's kept whole adds them to its saved list, the others had theirs cut.
    /// </summary>
    static void AddArtifacts(SaveGame game, PopulationData data)
    {
        var states = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        foreach (var town in game.Towns) if (!town.IsNest && town.Instance.Length > 0) states.TryAdd(town.Instance, town.Record);
        foreach (var (site, list) in PopulationSystem.PlanArtifacts(data))
            if (states.TryGetValue(data.Sites[site].InstanceId, out var state))
                PopulationSystem.WriteArtifacts(state, [.. PopulationSystem.ReadArtifacts(state, data.Db), .. list]);
    }

    /// <summary>
    /// The zone files (<c>FUN_140377710</c>): with "Buildings" each keeps the building instances that are removals or whose state is owned by the player faction, with their states; a zone
    /// that keeps none has no file. Without the tick no zone file stays. The loose items and every other building are the new world's.
    /// </summary>
    static void FilterZones(SaveGame game, string playerId, ImportOptions options)
    {
        if ((options & ImportOptions.Buildings) == 0) { game.Zones.Clear(); return; }
        foreach (var (coordinate, zone) in game.Zones.ToList())
        {
            var byId = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
            foreach (var r in zone.File.Records) byId[r.StringId] = r;
            if (zone.BuildingList is not { } list) { game.Zones.Remove(coordinate); continue; }
            list.Instances.RemoveAll(i => !i.IsRemoved && !(i.States.Select(s => byId.GetValueOrDefault(s)).FirstOrDefault(s => s?.RecordType == FcsRecordType.GamestateBuilding) is { } state
                && state.Strings.GetValueOrDefault("owner faction ID") == playerId));
            if (list.Instances.Count == 0) { game.Zones.Remove(coordinate); continue; }
            var used = list.Instances.SelectMany(i => i.States).ToHashSet(StringComparer.Ordinal);
            zone.File.Records.RemoveAll(r => !ReferenceEquals(r, list) && !used.Contains(r.StringId));
            zone.File.SlotLists.Clear();
        }
    }
}