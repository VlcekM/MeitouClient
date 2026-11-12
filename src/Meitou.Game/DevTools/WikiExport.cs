using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Game.Host;

namespace Meitou.Game.DevTools;

/// <summary>
/// <c>--wiki-export &lt;dir&gt;</c>: writes the agent wiki's generated files (zones, towns, factions, squads, npcs, items, buildings as JSON) from
/// the player's own game data, the load order of <c>data/mods.cfg</c> applied, and exits. No window, no game run. The files follow the schema of
/// <c>tools/playtest/wiki.example</c> plus an optional <c>data</c> object; nothing in them comes from outside the data files read
/// (docs/agent-playtest.md, "The wiki").
/// </summary>
static partial class WikiExport
{
    #region Export

    /// <summary>
    /// The source the export names when none is given.
    /// </summary>
    const string DefaultSource = "generated from your game data";

    /// <summary>
    /// The JSON writer's options: indented, relaxed escaping.
    /// </summary>
    static readonly JsonWriterOptions WriterOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// One exported entry: id, name, kind, faction, zone, position, confidence, notes, record id and its data.
    /// </summary>
    sealed record Row(string Id, string Name, string Kind, string? Faction, string? Zone, float? X, float? Z, string Confidence, string Notes,
        string? Record, SortedDictionary<string, object?> Data);

    /// <summary>
    /// Free-text fields the game data carries that <c>--facts-only</c> leaves out (item and research descriptions; the export reads no dialogue
    /// lines, lore or book texts at all).
    /// </summary>
    static readonly string[] DroppedProseFields = ["item description", "research description", "dialogue lines (never read)",
        "lore and book contents (never read)", "character and squad flavour text (never read)"];

    /// <summary>
    /// True under <c>--facts-only</c>: no description text from the game goes into the files.
    /// </summary>
    static bool s_factsOnly;

    /// <summary>
    /// Runs the export: loads the install's data (load order applied) and the world, builds every row list in order (ids are handed out in that
    /// order) and writes one JSON file per list into <paramref name="outDir"/>. Returns the process exit code (0).
    /// </summary>
    public static int Run(string outDir, GameInstall install, bool factsOnly = false)
    {
        s_factsOnly = factsOnly;
        Directory.CreateDirectory(outDir);
        var order = LoadOrder.FromInstall(install);
        var db = GameDatabase.Load(order);
        var world = WorldLevelData.Load(Path.Combine(install.DataDirectory, WorldLevelData.RelativeDirectory), order.Entries, includeInteriors: true);
        string source = $"{DefaultSource} by Meitou {BuildVersion.Tag} ({BuildVersion.Commit})";
        var ids = new RowIds();
        string FactionName(string? id) => id is not null && db.Find(id) is { } f ? f.Name : "";

        // Towns: every placed town record except nest markers and null towns.
        var towns = new List<Row>();
        var placedTowns = new List<PlacedTown>();
        var townZones = new Dictionary<ZoneCoordinate, List<string>>();
        var townAt = new Dictionary<string, (float X, float Z)>(StringComparer.Ordinal);
        TownRows(db, world, ids.Id, FactionName, towns, placedTowns, townZones, townAt);

        // Zones: the grid cells that hold at least one town.
        var zones = ZoneRows(townZones, ids);

        // Biome groups with the squads they spawn (WikiExport.Stats.cs); the zone cells and towns carry their biome.
        var templateById = new Dictionary<string, SquadTemplate>(StringComparer.Ordinal);
        foreach (var t in SquadTemplate.LoadAll(db)) templateById.TryAdd(t.Id, t);
        var spawn = Spawns(db, install, placedTowns, templateById, ids.Id);
        MarkBiomes(spawn, zones);
        MarkBiomes(spawn, towns);

        // Factions.
        var factions = FactionRows(db, towns, ids.Id);

        // Town detail (buildings, shops, houses for sale), town overrides, unique and bounty characters (WikiExport.Detail.cs).
        var detail = Detail(db, world, placedTowns, ids.Id);
        var characterRecords = detail.Characters.Select(c => c.Record ?? "").ToHashSet(StringComparer.Ordinal);

        // Squad templates and the unique characters that live in towns.
        var (squads, npcs) = SquadRows(db, world, towns, townAt, characterRecords, ids.Id, FactionName);

        // Character types (humans and animals), without position.
        CharacterTypeRows(db, npcs, characterRecords, ids.Id, FactionName);

        // Research tree, items with their stats and blueprints, animals (WikiExport.Stats.cs).
        var (research, unlocks) = ResearchRows(db, ids.Id);
        var items = Items(db, ids.Id, unlocks);
        var animals = Animals(db, ids.Id, templateById, spawn);

        // Buildings (named records only).
        var buildings = BuildingRecordRows(db, ids.Id);

        var files = new (string Name, List<Row> Rows)[] { ("zones", zones), ("towns", towns), ("factions", factions), ("squads", squads),
            ("npcs", npcs), ("items", items), ("buildings", buildings), ("shops", detail.Shops), ("vendors", detail.Vendors),
            ("overrides", detail.Overrides), ("characters", detail.Characters), ("research", research),
            ("animals", animals), ("spawns", spawn.Rows) };
        WriteFiles(outDir, install, factsOnly, order, source, files);
        return 0;
    }

    /// <summary>
    /// The entry ids handed out so far in one export: a name's slug (else the record id's, else "entry"), made unique with the record id and then
    /// a counter.
    /// </summary>
    sealed class RowIds
    {
        /// <summary>
        /// Every id handed out or reserved.
        /// </summary>
        readonly HashSet<string> _used = new(StringComparer.Ordinal);

        /// <summary>
        /// A new unique id for the entry named <paramref name="name"/> of record <paramref name="recordId"/>.
        /// </summary>
        public string Id(string name, string recordId)
        {
            string slug = Slug(name);
            if (slug.Length == 0) slug = Slug(recordId);
            if (slug.Length == 0) slug = "entry";
            string id = slug;
            if (_used.Contains(id)) id = slug + "-" + Slug(recordId);
            for (int n = 2; _used.Contains(id); n++) id = $"{slug}-{Slug(recordId)}-{n}";
            _used.Add(id);
            return id;
        }

        /// <summary>
        /// Marks <paramref name="id"/> as taken (the zone ids, which are fixed).
        /// </summary>
        public void Reserve(string id) => _used.Add(id);
    }

    /// <summary>
    /// Zones: the grid cells that hold at least one town, by row then column, each with its centre and the towns inside.
    /// </summary>
    static List<Row> ZoneRows(Dictionary<ZoneCoordinate, List<string>> townZones, RowIds ids)
    {
        var zones = new List<Row>();
        foreach (var (zone, names) in townZones.OrderBy(z => z.Key.Y).ThenBy(z => z.Key.X))
        {
            var (ox, oz) = WorldLayout.ZoneOrigin(zone);
            string zid = $"zone-{zone.X}-{zone.Y}";
            ids.Reserve(zid);
            zones.Add(new Row(zid, $"Zone {zone.X}.{zone.Y}", "zone", null, null, (float)(ox + WorldLayout.ZoneSize / 2.0),
                (float)(oz + WorldLayout.ZoneSize / 2.0), "Observed",
                $"Grid cell zone.{zone.X}.{zone.Y}.zone, centre given; edge {WorldLayout.ZoneSize} units. "
                + $"Towns inside: {string.Join(", ", names.Distinct().Take(12))}.", null,
                new SortedDictionary<string, object?> { ["towns"] = names.Count, ["file"] = zone.FileName }));
        }
        return zones;
    }

    /// <summary>
    /// Sets each positioned row's <c>biome</c> to the biome group painted at its position, when there is one.
    /// </summary>
    static void MarkBiomes(SpawnIndex spawn, List<Row> rows)
    {
        foreach (var r in rows)
            if (r.X is { } x && r.Z is { } z && BiomeAt(spawn, x, z) is { Length: > 0 } biome) r.Data["biome"] = biome;
    }

    /// <summary>
    /// The name of the biome group painted at world position (<paramref name="x"/>, <paramref name="z"/>), "" when none.
    /// </summary>
    static string BiomeAt(SpawnIndex spawn, float x, float z)
    {
        var (cx, cz) = WeatherAreas.CellOf(x, z);
        return spawn.Map.GroupOf(cx, cz) is >= 0 and var g ? spawn.Map.Groups[g].Name : "";
    }

    /// <summary>
    /// Character types: one npc (or animal) row without position for every named character record not already a squad leader's npc row or a
    /// unique or bounty character.
    /// </summary>
    static void CharacterTypeRows(GameDatabase db, List<Row> npcs, HashSet<string> characterRecords, Func<string, string, string> id,
        Func<string?, string> factionName)
    {
        var seenCharacters = new HashSet<string>(npcs.Select(n => n.Record ?? ""), StringComparer.Ordinal);
        foreach (var record in db.OfType(FcsRecordType.Character).OrderBy(r => r.Name, StringComparer.Ordinal)
                .ThenBy(r => r.StringId, StringComparer.Ordinal))
        {
            if (record.Name.Length == 0 || seenCharacters.Contains(record.StringId) || characterRecords.Contains(record.StringId))
                continue;
            string faction = record.GetReferences("faction") is { Count: > 0 } fl ? factionName(fl[0].TargetStringId) : "";
            bool animal = record.Type == FcsRecordType.AnimalCharacter;
            npcs.Add(new Row(id(record.Name, record.StringId), record.Name, animal ? "animal" : "npc", faction.Length == 0 ? null : faction, null,
                null, null, "Observed",
                (animal ? "Animal" : "Character") + " record" + (faction.Length == 0 ? "" : $" of faction {faction}")
                + "; no position (spawned by squads and towns).", record.StringId,
                new SortedDictionary<string, object?> { ["record"] = record.StringId, ["definedBy"] = record.DefinedBy }));
        }
    }

    /// <summary>
    /// Buildings: one row per named building record, by name then id.
    /// </summary>
    static List<Row> BuildingRecordRows(GameDatabase db, Func<string, string, string> id)
    {
        var buildings = new List<Row>();
        foreach (var record in db.OfType(FcsRecordType.Building).OrderBy(r => r.Name, StringComparer.Ordinal)
                .ThenBy(r => r.StringId, StringComparer.Ordinal))
        {
            if (record.Name.Length == 0) continue;
            buildings.Add(new Row(id(record.Name, record.StringId), record.Name, "building", null, null, null, null, "Observed",
                $"Building record {record.StringId}, defined by {record.DefinedBy}.", record.StringId,
                new SortedDictionary<string, object?> { ["record"] = record.StringId, ["definedBy"] = record.DefinedBy,
                    ["value"] = Num(record, "value") }));
        }
        return buildings;
    }

    /// <summary>
    /// Towns: every placed town record except nest markers and null towns, with the zones that hold them and where each town is.
    /// </summary>
    static void TownRows(GameDatabase db, WorldLevelData world, Func<string, string, string> id, Func<string?, string> factionName,
        List<Row> towns, List<PlacedTown> placedTowns, Dictionary<ZoneCoordinate, List<string>> townZones,
        Dictionary<string, (float X, float Z)> townAt)
    {
        foreach (var placement in world.Towns().OrderBy(p => p.TownId, StringComparer.Ordinal).ThenBy(p => p.InstanceId, StringComparer.Ordinal))
        {
            if (db.Find(placement.TownId) is not { Type: FcsRecordType.Town } record) continue;
            var town = TownData.From(record);
            if (town.Type is TownType.NestMarker or TownType.Null || town.Name.Length == 0) continue;
            var zone = WorldLayout.ZoneOf(placement.Position.X, placement.Position.Z);
            string zoneId = $"zone-{zone.X}-{zone.Y}";
            var data = new SortedDictionary<string, object?>
            {
                ["townType"] = town.Type.ToString(),
                ["isPublic"] = town.IsPublic,
                ["radius"] = (int)town.Radius,
                ["residents"] = Names(db, town.Residents.Select(l => l.Id)),
                ["bar"] = Names(db, town.BarSquads.Select(l => l.Id)),
                ["roaming"] = Names(db, town.RoamingSquads.Select(l => l.Id)),
                ["record"] = placement.TownId,
            };
            string faction = factionName(town.Faction);
            var row = new Row(id(town.Name, placement.TownId), town.Name,
                town.Type switch { TownType.Ruins => "ruins", TownType.Nest => "nest",
                TownType.Outpost or TownType.Military or TownType.Prison or TownType.SlaveCamp => "camp", TownType.PointOfInterest => "poi",
                _ => "town" }, faction.Length == 0 ? null : faction, zoneId, placement.Position.X, placement.Position.Z, "Observed",
                $"{town.Type} in {zoneId}" + (faction.Length == 0 ? "" : $", faction {faction}")
                + ". Position as the level data places it; not checked in game.", placement.TownId, data);
            towns.Add(row);
            placedTowns.Add(new PlacedTown(placement, town, row));
            townAt.TryAdd(placement.TownId, (placement.Position.X, placement.Position.Z));
            if (!townZones.TryGetValue(zone, out var list)) townZones[zone] = list = [];
            list.Add(town.Name);
        }
    }

    /// <summary>
    /// Factions: every named faction record with its relations and how many towns it holds.
    /// </summary>
    static List<Row> FactionRows(GameDatabase db, List<Row> towns, Func<string, string, string> id)
    {
        var factions = new List<Row>();
        var player = db.Find(FactionData.PlayerId);
        foreach (var record in db.OfType(FcsRecordType.Faction).OrderBy(r => r.Name, StringComparer.Ordinal)
                .ThenBy(r => r.StringId, StringComparer.Ordinal))
        {
            var f = FactionData.From(record);
            if (f.Name.Length == 0) continue;
            var relations = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (other, value) in f.Relations)
                if (db.Find(other) is { Type: FcsRecordType.Faction } o && o.Name.Length > 0) relations[o.Name] = value;
            var data = new SortedDictionary<string, object?>
            {
                ["fundamentalType"] = f.FundamentalType.ToString(),
                ["defaultRelation"] = f.DefaultRelation,
                ["relations"] = relations,
                ["notReal"] = f.NotReal,
                ["offersBounties"] = f.BooksBounties,
                ["towns"] = towns.Count(t => t.Faction == f.Name),
                ["record"] = record.StringId,
            };
            string toPlayer = relations.TryGetValue(player?.Name ?? "", out var rel) ? $" Explicit relation to your faction {rel}." : "";
            factions.Add(new Row(id(f.Name, record.StringId), f.Name, "faction", null, null, null, null, "Observed",
                $"{f.FundamentalType} faction, default relation {f.DefaultRelation}.{toPlayer}"
                + (f.NotReal ? " Not a real faction (wildlife, ruins, slaves)." : ""), record.StringId, data));
        }
        return factions;
    }

    /// <summary>
    /// Squad templates, and the leaders who lead exactly one resident squad (one npc entry each, at the town's position).
    /// </summary>
    static (List<Row> Squads, List<Row> Npcs) SquadRows(GameDatabase db, WorldLevelData world, List<Row> towns,
        Dictionary<string, (float X, float Z)> townAt, HashSet<string> characterRecords, Func<string, string, string> id,
        Func<string?, string> factionName)
    {
        var squads = new List<Row>();
        var npcs = new List<Row>();
        var residentOf = new Dictionary<string, string>(StringComparer.Ordinal);   // squad id -> town record
        foreach (var t in world.Towns())
        {
            if (db.Find(t.TownId) is { Type: FcsRecordType.Town } tr)
            {
                foreach (var l in TownData.From(tr).Residents.Concat(TownData.From(tr).BarSquads))
                    residentOf.TryAdd(l.Id, t.TownId);
            }
        }
        var allSquads = SquadTemplate.LoadAll(db);
        var townFactionOf = new Dictionary<string, string>(StringComparer.Ordinal);   // town record -> faction name
        foreach (var t in towns) if (t.Faction is { } tf && t.Record is { } trec) townFactionOf.TryAdd(trec, tf);
        // A leader record that leads squads in several towns is a generic role (a shop trader): no position for it.
        var leaderUse = allSquads.Where(q => q.Leader is not null && residentOf.ContainsKey(q.Id))
            .GroupBy(q => q.Leader!.Value.Id)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var squad in allSquads.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            if (squad.Name.Length == 0) continue;
            string leader = squad.Leader is { } ll && db.Find(ll.Id) is { } lc ? lc.Name : "";
            string faction = factionName(squad.Faction);
            string factionSource = faction.Length > 0 ? "squad template" : "";
            if (faction.Length == 0 && residentOf.TryGetValue(squad.Id, out var ownerTown)
                    && townFactionOf.TryGetValue(ownerTown, out var ownerFaction))
            {
                faction = ownerFaction;
                factionSource = "the town that lists the squad (a template without a faction takes its town's)";
            }
            var data = new SortedDictionary<string, object?>
            {
                ["factionSource"] = factionSource,
                ["unique"] = squad.IsUnique,
                ["leader"] = leader,
                ["trader"] = squad.HasShop,
                ["members"] = Names(db, squad.Squad.Concat(squad.Squad2).Select(l => l.Id)),
                ["record"] = squad.Id,
            };
            bool home = residentOf.TryGetValue(squad.Id, out var townRecord) && townAt.ContainsKey(townRecord);
            float? x = home ? townAt[townRecord!].X : null, z = home ? townAt[townRecord!].Z : null;
            string where = home
                ? $" Listed as a resident of {db.Find(townRecord!)?.Name}; the position is that town's, not where the squad stands."
                : "";
            var row = new Row(id(squad.Name, squad.Id), squad.Name, squad.IsUnique ? "unique-squad" : "squad", faction.Length == 0 ? null : faction,
                null, x, z, home ? "Inferred" : "Observed",
                (squad.IsUnique ? "Unique squad" : "Squad template") + (leader.Length > 0 ? $", leader {leader}" : "")
                + (squad.HasShop ? ", runs a shop" : "") + "." + where, squad.Id, data);
            squads.Add(row);
            // A leader who leads exactly one resident squad is a person of that town: one npc entry with the town's position.
            if (leader.Length > 0 && home && !characterRecords.Contains(squad.Leader!.Value.Id)
                    && leaderUse.GetValueOrDefault(squad.Leader!.Value.Id) == 1)
            {
                var zone = WorldLayout.ZoneOf(x!.Value, z!.Value);
                npcs.Add(new Row(id(leader, squad.Leader!.Value.Id), leader, "npc", faction.Length == 0 ? null : faction,
                    $"zone-{zone.X}-{zone.Y}", x, z, "Inferred",
                    $"Leader of the squad {squad.Name}, a resident of {db.Find(townRecord!)?.Name}. Position is the town's; roaming people move.",
                        squad.Leader.Value.Id,
                    new SortedDictionary<string, object?> { ["squad"] = squad.Name, ["town"] = db.Find(townRecord!)?.Name,
                        ["factionSource"] = factionSource, ["record"] = squad.Leader.Value.Id }));
            }
        }
        return (squads, npcs);
    }

    /// <summary>
    /// Writes one JSON file per row list with the export's meta block (load order, mods, issues) and reports the counts.
    /// </summary>
    static void WriteFiles(string outDir, GameInstall install, bool factsOnly, LoadOrder order, string source,
        (string Name, List<Row> Rows)[] files)
    {
        var modNames = order.Entries.Select(e => e.Name).Where(n => !LoadOrder.BaseFiles.Contains(n) && !IsSeparator(n)).ToList();
        // A facts-only export is shipped, so it names no local path: only the clean-install wording or the mod names.
        string dataDir = factsOnly
            ? (modNames.Count == 0 ? "clean install (base game + official DLC files only)" : "base game + mods: " + string.Join(", ", modNames))
            : install.DataDirectory;
        string modsCfgPath = ModList.ModsCfgPath(install);
        var modsCfg = File.Exists(modsCfgPath) ? ModList.ParseModsCfg(File.ReadAllLines(modsCfgPath)).ToList() : [];
        var meta = new Meta(source, dataDir, string.Join(", ", order.Entries.Select(e => e.Name)),
            [.. order.Entries.Select(e => e.Name).Where(n => !LoadOrder.BaseFiles.Contains(n) && !IsSeparator(n))], modsCfg,
            order.Issues.Select(i => $"{i.File}: {i.Message}").ToList(), factsOnly);
        var counts = new List<string>();
        foreach (var (name, rows) in files)
        {
            Write(Path.Combine(outDir, name + ".json"), rows, meta);
            counts.Add($"{name} {rows.Count}");
        }
        Console.WriteLine($"wiki-export: {string.Join(", ", counts)}");
        Console.WriteLine($"wiki-export: data {dataDir}; {order.Entries.Count} data files; mods beyond the base "
            + $"game: {(meta.Mods.Count == 0 ? "none (clean install)" : string.Join(", ", meta.Mods))}; wrote {outDir}");
    }

    #endregion

    #region Writing

    /// <summary>
    /// The divider lines of a mod manager's load order ("[1]----------[UI]----------.mod") are entries of mods.cfg but no mod.
    /// </summary>
    static bool IsSeparator(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, @"^\[\d+\]-+\[.*\]-+\.mod$");

    /// <summary>
    /// What every generated file says it was made from: the data folder, the load order, and the mods beyond the base game's four data files
    /// (<c>mods.cfg</c> applied).
    /// </summary>
    sealed record Meta(string Source, string DataDir, string LoadOrder, List<string> Mods, List<string> ModsCfg, List<string> Issues, bool FactsOnly);

    /// <summary>
    /// Writes the export file: the source, data folder, load order, mods, load issues, the facts-only flag and dropped fields, the time and the
    /// entries.
    /// </summary>
    static void Write(string path, List<Row> rows, Meta meta)
    {
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream, WriterOptions);
        w.WriteStartObject();
        w.WriteString("source", meta.Source);
        w.WriteString("dataDir", meta.DataDir);
        w.WriteString("loadOrder", meta.LoadOrder);
        w.WriteBoolean("modded", meta.Mods.Count > 0);
        w.WriteString("modsSummary", meta.Mods.Count == 0
            ? "no mods beyond the base game (clean install)"
            : $"{meta.Mods.Count} mod file(s) beyond the base game, from mods.cfg");
        w.WriteStartArray("mods");
        foreach (string m in meta.Mods) w.WriteStringValue(m);
        w.WriteEndArray();
        w.WriteStartArray("modsCfg");
        foreach (string m in meta.ModsCfg) w.WriteStringValue(m);
        w.WriteEndArray();
        w.WriteStartArray("loadIssues");
        foreach (string m in meta.Issues) w.WriteStringValue(m);
        w.WriteEndArray();
        w.WriteBoolean("factsOnly", meta.FactsOnly);
        w.WriteStartArray("dropped_prose_fields");
        if (meta.FactsOnly) foreach (string f in DroppedProseFields) w.WriteStringValue(f);
        w.WriteEndArray();
        w.WriteString("generated", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        w.WriteStartArray("entries");
        foreach (var r in rows)
        {
            w.WriteStartObject();
            w.WriteString("id", r.Id);
            w.WriteString("name", r.Name);
            w.WriteStartArray("aliases");
            if (r.Record is not null) w.WriteStringValue(r.Record);
            w.WriteEndArray();
            w.WriteString("kind", r.Kind);
            Nullable(w, "faction", r.Faction);
            w.WriteNull("region");
            if (r.X is { } x && r.Z is { } z)
            {
                w.WriteNumber("x", MathF.Round(x));
                w.WriteNumber("z", MathF.Round(z));
            }
            else
            {
                w.WriteNull("x");
                w.WriteNull("z");
            }
            Nullable(w, "zone", r.Zone);
            w.WriteBoolean("verified", false);
            w.WriteNull("verifiedOn");
            w.WriteString("seen", "");
            w.WriteString("source", "generated from game data");
            w.WriteString("confidence", r.Confidence);
            w.WriteString("notes", r.Notes);
            w.WritePropertyName("data");
            WriteValue(w, r.Data);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>
    /// Writes <paramref name="value"/> as <paramref name="name"/>, or null.
    /// </summary>
    static void Nullable(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }

    /// <summary>
    /// Writes a data value: null, text, a bool, a number (floats and doubles to three decimals), an object, a list, else its invariant text.
    /// </summary>
    static void WriteValue(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null:
                w.WriteNullValue();
                break;
            case string s:
                w.WriteStringValue(s);
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case int i:
                w.WriteNumberValue(i);
                break;
            case long l:
                w.WriteNumberValue(l);
                break;
            case float f:
                w.WriteNumberValue(MathF.Round(f * 1000) / 1000);
                break;
            case double dbl:
                w.WriteNumberValue(Math.Round(dbl, 3));
                break;
            case IDictionary<string, object?> d:
                w.WriteStartObject();
                foreach (var (k, x) in d)
                {
                    w.WritePropertyName(k);
                    WriteValue(w, x);
                }
                w.WriteEndObject();
                break;
            case IEnumerable<string> list:
                w.WriteStartArray();
                foreach (var s in list) w.WriteStringValue(s);
                w.WriteEndArray();
                break;
            case System.Collections.IEnumerable items:
                w.WriteStartArray();
                foreach (var x in items) WriteValue(w, x);
                w.WriteEndArray();
                break;
            default:
                w.WriteStringValue(Convert.ToString(v, CultureInfo.InvariantCulture));
                break;
        }
    }

    #endregion

    #region Value helpers

    /// <summary>
    /// A float or int field as a number (floats rounded to three decimals), null when the record has neither.
    /// </summary>
    static object? Num(GameRecord r, string key) =>
        r.Floats.TryGetValue(key, out float f) ? MathF.Round(f * 1000) / 1000 : r.Ints.TryGetValue(key, out int i) ? i : null;

    /// <summary>
    /// The names of the records <paramref name="ids"/> (the known, distinct, at most 20).
    /// </summary>
    static List<string> Names(GameDatabase db, IEnumerable<string> ids) =>
        [.. ids.Select(i => db.Find(i)?.Name ?? "").Where(n => n.Length > 0).Distinct().Take(20)];

    /// <summary>
    /// <paramref name="text"/> as a slug: lower-case letters and digits, accents dropped, every other run a single dash.
    /// </summary>
    static string Slug(string text)
    {
        var sb = new StringBuilder();
        bool dash = false;
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(c);
                dash = false;
            }
            else if (c is >= 'A' and <= 'Z')
            {
                sb.Append(char.ToLowerInvariant(c));
                dash = false;
            }
            else if (!dash && sb.Length > 0 && char.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append('-');
                dash = true;
            }
        }
        return sb.ToString().Trim('-');
    }

    #endregion
}