using System.Numerics;
using Meitou.Content;
using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>
/// The placed contents of the world from <c>data/newland/leveldata/</c> (docs/formats/zones.md, "Mods"): zone files (buildings),
/// <c>leveldata.level</c> (towns, roads) and optionally <c>interiors.level</c>. Files come in layers, one per game-data file in load order:
/// the folder itself for <c>gamedata.base</c>, a core file's sub-folder named after it (<c>Newwworld</c>, <c>Dialogue</c>, <c>rebirth</c>),
/// a mod's <c>leveldata</c> folder beside its <c>.mod</c> file. Each file is merged over the earlier layers' with the FCS rules of
/// <see cref="GameDatabase"/>, as the game reads level files with its game-data loader.
/// </summary>
public sealed class WorldLevelData
{
    /// <summary>
    /// Folder of the world's level data, relative to the install's <c>data/</c> folder.
    /// </summary>
    public const string RelativeDirectory = "newland/leveldata";

    /// <summary>
    /// The level file of towns and roads in each layer's folder.
    /// </summary>
    public const string LevelFileName = "leveldata.level";
    /// <summary>
    /// The building layouts file in each layer's folder.
    /// </summary>
    public const string InteriorsFileName = "interiors.level";

    /// <summary>
    /// Record holding the placed buildings of a zone file.
    /// </summary>
    public const string BuildingListId = "0-buildinglist";

    /// <summary>
    /// Layers in the order they were applied.
    /// </summary>
    public List<WorldLevelLayer> Layers { get; } = [];

    /// <summary>
    /// Whether <see cref="ResidentRuins.Apply"/> has set the ruins the residents make.
    /// </summary>
    public bool ResidentRuinsApplied { get; internal set; }

    /// <summary>
    /// The factions' building swaps and each placement's owner faction by key, once <see cref="BuildingSwapPass.ApplyBuildingSwaps"/> has run: every
    /// <see cref="BuildingLayouts"/> read from these levels swaps its furniture with them (<see cref="BuildingLayouts.UseSwaps(WorldLevelData)"/>),
    /// so the drawn world, the town buildings and the navmesh have the same pieces. Null: not applied, or the data has no swaps.
    /// </summary>
    public (Gameplay.BuildingSwaps Swaps, IReadOnlyDictionary<string, string> Owners)? FurnitureSwaps { get; internal set; }

    /// <summary>
    /// The keys of the buildings <see cref="ResidentRuins.Apply"/> ruined (their level files have them standing).
    /// </summary>
    public HashSet<string> ResidentRuined { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The keys of the buildings <see cref="ResidentRuins.Apply"/> stood up (their level files have them destroyed).
    /// </summary>
    public HashSet<string> ResidentRestored { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The interior and exterior layout names the level files give the buildings <see cref="ResidentRuins.Apply"/> named otherwise, by building key.
    /// </summary>
    public Dictionary<string, (string Interior, string Exterior)> LevelLayoutNames { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The name and squad template the level files give the buildings <see cref="ResidentRuins.Apply"/> named otherwise, by building key.
    /// </summary>
    public Dictionary<string, BuildingNaming> LevelNames { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Merged zone files by grid cell.
    /// </summary>
    public SortedDictionary<ZoneCoordinate, GameDatabase> Zones { get; } =
        new(Comparer<ZoneCoordinate>.Create((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X)));

    /// <summary>
    /// Merged <c>leveldata.level</c> files: town list, town states, roads.
    /// </summary>
    public GameDatabase Level { get; } = new();

    /// <summary>
    /// Merged <c>interiors.level</c> files, or null when not loaded.
    /// </summary>
    public GameDatabase? Interiors { get; private set; }

    /// <summary>
    /// Per zone, what a save's zone file carries over from the level files besides the merged records (docs/formats/save.md "Zone files"): the last
    /// layer's slot list (the trailer) and the highest <c>nextId</c> of the layers.
    /// </summary>
    public Dictionary<ZoneCoordinate, ZoneLevelInfo> ZoneInfo { get; } = [];

    /// <summary>
    /// Merge problems over all files (records defined twice, ...), tagged with the layer file.
    /// </summary>
    public List<GameDataIssue> Issues { get; } = [];

    /// <summary>
    /// The <see cref="Issues"/> worth a warning: not a record defined again, which is how a later layer replaces placements (Observed: the
    /// unmodded 1.0.68 layers give 4047, all of that kind, and the game plays as shipped).
    /// </summary>
    public List<GameDataIssue> Problems() => [.. Issues.Where(i => i.Kind != GameDataIssueKind.RecordAlreadyDefined)];

    /// <summary>
    /// Counts the changes <see cref="ApplyEdit"/> made; an index of the buildings made earlier is stale when it differs.
    /// </summary>
    public int EditRevision { get; private set; }

    /// <summary>
    /// Merges <paramref name="data"/> into the zone's records over everything loaded, as if another layer's zone file had been there at load (the
    /// level editor, docs/game/level-editor.md: its placed buildings and its cleared ones, which a later layer empties). Call it on the thread that
    /// owns the world's changes; readers on other threads may meet the zone mid-change.
    /// </summary>
    public void ApplyEdit(ZoneCoordinate zone, FcsFile data, string name)
    {
        if (!Zones.TryGetValue(zone, out var db)) Zones[zone] = db = new GameDatabase();
        db.Apply(data, name);
        if (!ZoneInfo.TryGetValue(zone, out var info)) ZoneInfo[zone] = info = new ZoneLevelInfo();
        info.NextId = Math.Max(info.NextId, data.NextId);
        EditRevision++;
    }

    /// <summary>
    /// The height the original stands each outdoor level building at, by <see cref="BuildingKey"/> (docs/formats/zones.md "Placement height"); filled
    /// by <see cref="PlaceOnGround"/>. A building without one keeps its state's <c>world Y pos</c>.
    /// </summary>
    readonly Dictionary<string, float> _groundHeights = new(StringComparer.Ordinal);

    /// <summary>
    /// The original deletes a building whose instance Y is below this when it makes it ("under the ground", docs/formats/zones.md "Placement
    /// height").
    /// </summary>
    public const float UnderGround = -1000;

    #region Loading

    /// <summary>
    /// Loads the install's world with its outdoor buildings stood on the ground (<see cref="PlaceOnGround"/>).
    /// </summary>
    public static WorldLevelData Load(GameInstall install, bool includeInteriors = false) =>
        Load(install, LoadOrder.FromInstall(install), includeInteriors);

    /// <summary>
    /// <see cref="Load(GameInstall, bool)"/> under another load order (a feedback report's, for the probe tools).
    /// </summary>
    public static WorldLevelData Load(GameInstall install, LoadOrder order, bool includeInteriors = false)
    {
        string directory = Path.Combine(install.DataDirectory, RelativeDirectory);
        var world = Load(directory, order.Entries, includeInteriors);
        return OnGround(world, TerrainHeightmap.PathOf(install));
    }

    /// <summary>
    /// The base game's world alone (the core files' layers, no mod's): what the install tests are written against.
    /// </summary>
    public static WorldLevelData LoadBaseGame(GameInstall install, bool includeInteriors = false)
    {
        string directory = Path.Combine(install.DataDirectory, RelativeDirectory);
        var world = Load(directory, LoadOrder.BaseGame(install).Entries, includeInteriors);
        return OnGround(world, GameFiles.Resolve(Path.Combine(install.DataDirectory, TerrainHeightmap.RelativePath)));
    }

    /// <summary>
    /// <paramref name="world"/> with its buildings stood on the heightmap at <paramref name="path"/> (<see cref="PlaceOnGround"/>): the one the
    /// game reads, or for the base game the install's own; unchanged when the heightmap is missing.
    /// </summary>
    static WorldLevelData OnGround(WorldLevelData world, string path)
    {
        if (!File.Exists(path)) return world;

        using var map = TerrainHeightmap.Open(path);
        world.PlaceOnGround(map.BuildingGroundAt);
        return world;
    }

    /// <summary>
    /// Stands every outdoor level building on <paramref name="ground"/> (<see cref="TerrainHeightmap.BuildingGroundAt"/>) plus its instance Y, and
    /// one inside or mounted on another of its zone on that one's height plus its instance Y, as the original does whatever its state's <c>world Y
    /// pos</c> says (docs/formats/zones.md "Placement height"). One whose parent is not found keeps its state's height.
    /// </summary>
    public void PlaceOnGround(Func<double, double, float> ground)
    {
        _groundHeights.Clear();
        foreach (var (zone, db) in Zones)
        {
            if (db.Find(BuildingListId) is { } list)
                new PlacementHeights(zone, db, list, ground, _groundHeights).Resolve();
        }
    }

    /// <summary>
    /// A mod's own world layer, under its folder (docs/formats/zones.md "Mods").
    /// </summary>
    public const string ModDirectory = "leveldata";

    /// <summary>
    /// Loads from <paramref name="directory"/>, with sub-folder layers named after <paramref name="loadOrder"/> files (extension dropped).
    /// </summary>
    public static WorldLevelData Load(string directory, IEnumerable<string> loadOrder, bool includeInteriors = false) =>
        Load(directory, loadOrder.Select(name => new LoadOrderEntry(name, "")), includeInteriors);

    /// <summary>
    /// Loads from <paramref name="directory"/>, then one layer per <paramref name="loadOrder"/> file that has one, in load order: a core file's
    /// sub-folder named after it (extension dropped), a mod's <c>leveldata</c> folder beside its <c>.mod</c> file (Workshop or <c>mods/</c>). A file
    /// that would not load (<see cref="FcsGameLoader.Opens"/>) adds none.
    /// </summary>
    public static WorldLevelData Load(string directory, IEnumerable<LoadOrderEntry> loadOrder, bool includeInteriors = false)
    {
        var world = new WorldLevelData();
        world.Layers.Add(new WorldLevelLayer("", directory));
        foreach (var entry in loadOrder)
        {
            // A core .mod or a mod the game failed to load has no layer; gamedata.base keeps the base folder's (docs/formats/zones.md "Mods").
            if (entry.Path.Length > 0 && !FcsGameLoader.Opens(entry.Path)) continue;

            string name = Path.GetFileNameWithoutExtension(entry.Name);
            bool core = entry.Path.Length == 0 || LoadOrder.BaseFiles.Contains(entry.Name, StringComparer.OrdinalIgnoreCase);
            var sub = core
                ? Path.Combine(directory, name)
                : GameFiles.Resolve(Path.Combine(Path.GetDirectoryName(entry.Path)!, ModDirectory));
            if (Directory.Exists(sub))
                world.Layers.Add(new WorldLevelLayer(name, sub));
        }

        if (includeInteriors) world.Interiors = new GameDatabase();

        world.ApplyLayers();
        return world;
    }

    /// <summary>
    /// How many level files <see cref="ApplyLayers"/> decodes ahead of the one it merges.
    /// </summary>
    static int ReadAhead => Math.Max(2 * Environment.ProcessorCount, 4);

    /// <summary>
    /// Merges every layer's zone files, level file and interiors file into the world, layer by layer in order. The files are read and decoded on
    /// workers, up to <see cref="ReadAhead"/> ahead of the one being merged, and merged one at a time in that order, so the result is the
    /// sequential merge's (a 655-mod list has 179 mod layers; decoding was most of their load).
    /// </summary>
    void ApplyLayers()
    {
        var files = new List<(GameDatabase Db, string Path, WorldLevelLayer Layer, ZoneCoordinate? Zone)>();
        foreach (var layer in Layers)
        {
            foreach (var path in layer.ZoneFiles())
            {
                if (!ZoneCoordinate.TryParseFileName(path, out var zone)) continue;
                if (!Zones.TryGetValue(zone, out var db))
                    Zones[zone] = db = new GameDatabase();
                files.Add((db, path, layer, zone));
            }

            var level = GameFiles.Resolve(Path.Combine(layer.Directory, LevelFileName));
            if (File.Exists(level)) files.Add((Level, level, layer, null));
            var interiors = GameFiles.Resolve(Path.Combine(layer.Directory, InteriorsFileName));
            if (Interiors is not null && File.Exists(interiors)) files.Add((Interiors, interiors, layer, null));
        }

        var reads = new Task<(LevelFile? File, Exception? Refused)>?[files.Count];
        int started = 0;
        void ReadUpTo(int end)
        {
            for (; started < Math.Min(end, files.Count); started++)
            {
                string path = files[started].Path;
                reads[started] = Task.Run(() => Read(path));
            }
        }

        ReadUpTo(ReadAhead);
        for (int i = 0; i < files.Count; i++)
        {
            var read = reads[i]!.GetAwaiter().GetResult();
            reads[i] = null;
            ReadUpTo(i + 1 + ReadAhead);
            var (db, path, layer, zone) = files[i];
            Apply(db, path, layer, read, zone);
        }

        // A level file, or the reason the reader refuses it (any other failure is thrown).
        static (LevelFile? File, Exception? Refused) Read(string path)
        {
            try
            {
                return (LevelFile.ReadFile(path), null);
            }
            catch (Exception e) when (e is FcsFormatException or EndOfStreamException or InvalidDataException or IOException)
            {
                return (null, e);
            }
        }
    }

    /// <summary>
    /// Merges one level file of <paramref name="layer"/> (read as <paramref name="read"/>) into <paramref name="db"/>; a file the reader refuses is
    /// left out.
    /// </summary>
    void Apply(GameDatabase db, string path, WorldLevelLayer layer, (LevelFile? File, Exception? Refused) read, ZoneCoordinate? zone = null)
    {
        var name = layer.Name.Length == 0 ? Path.GetFileName(path) : $"{layer.Name}/{Path.GetFileName(path)}";
        int before = db.Issues.Count;
        if (read.File is not { } file)
        {
            // A mod's file the reader refuses is left out (Observed: Great Frontiers' interiors.level has every zero byte turned to a space;
            // the game plays on).
            Issues.Add(new GameDataIssue(GameDataIssueKind.LevelFileNotLoaded, name, null, read.Refused!.Message));
            return;
        }

        if (zone is { } z)
        {
            if (!ZoneInfo.TryGetValue(z, out var info)) ZoneInfo[z] = info = new ZoneLevelInfo();
            // A later layer's slot list replaces an earlier one's, as in the game's container (Observed: a save's list extends the last layer's,
            // not the union).
            info.Slots = file.Trailer is { } slots && file.TrailerEntryInts == 1 ? slots : [];
            info.NextId = Math.Max(info.NextId, file.Data.NextId);
        }

        db.Apply(file.Data, name);
        Issues.AddRange(db.Issues.Skip(before));
        layer.FileCount++;
    }

    #endregion

    #region Buildings

    /// <summary>
    /// Buildings placed by the zones' <see cref="BuildingListId"/> records: the entries whose target is not a
    /// record of the zone file itself (see <see cref="BuildingListEntries"/>).
    /// </summary>
    public IEnumerable<BuildingPlacement> Buildings() => BuildingListEntries().Where(b => !b.TargetsZoneRecord);

    /// <summary>
    /// Every entry of the zones' <see cref="BuildingListId"/> records. Besides placed buildings (target: a
    /// BUILDING record of the game data) these hold entries at the origin whose target is a record in the same
    /// zone file (e.g. an INVENTORY_STATE).
    /// </summary>
    public IEnumerable<BuildingPlacement> BuildingListEntries()
    {
        foreach (var (zone, db) in Zones)
        {
            if (db.Find(BuildingListId) is not { } list) continue;

            foreach (var instance in list.Instances.Values)
            {
                if (instance.IsCleared) continue; // emptied by a later layer
                if (instance.Position.Y < UnderGround) continue; // the original deletes it on load

                string? stateId = instance.States.Count > 0 ? instance.States[0] : null;
                float? worldY = WorldYOf(zone, db, instance, stateId);
                yield return new BuildingPlacement(zone, instance.Id, instance.Target, instance.Position, instance.Rotation, stateId, worldY)
                {
                    TargetsZoneRecord = db.Find(instance.Target) is not null,
                };
            }
        }
    }

    /// <summary>
    /// The absolute height of a placed building: the height the original stands it on when it was placed on the ground, else its state's
    /// <c>world Y pos</c>; null when neither is known.
    /// </summary>
    float? WorldYOf(ZoneCoordinate zone, GameDatabase db, GameInstance instance, string? stateId)
    {
        if (_groundHeights.TryGetValue(BuildingKey.Of(zone, instance.Id), out float onGround)) return onGround;
        if (stateId is not null && db.Find(stateId) is { } state && state.Floats.TryGetValue("world Y pos", out var y)) return y;

        return null;
    }

    #endregion

    #region Towns and roads

    /// <summary>
    /// Every town placed by a <see cref="FcsRecordType.GamestateTownInstanceList"/> record of the level files.
    /// </summary>
    public IEnumerable<TownPlacement> Towns()
    {
        foreach (var list in Level.OfType(FcsRecordType.GamestateTownInstanceList))
        {
            foreach (var instance in list.Instances.Values.Where(i => !i.IsCleared))
                yield return new TownPlacement(
                    instance.Id,
                    instance.Target,
                    instance.Position,
                    instance.States.Count > 0 ? instance.States[0] : null);
        }
    }

    /// <summary>
    /// Zones listed by <see cref="FcsRecordType.GamestateTown"/> records (<c>zones_count</c>, <c>zone_x_i</c>,
    /// <c>zone_y_i</c>), keyed by their <c>instance</c> field: the id of the town's placement.
    /// </summary>
    public Dictionary<string, List<ZoneCoordinate>> TownZones()
    {
        var result = new Dictionary<string, List<ZoneCoordinate>>(StringComparer.Ordinal);
        foreach (var town in Level.OfType(FcsRecordType.GamestateTown))
        {
            var instance = town.GetString("instance");
            if (instance.Length == 0) continue;

            var zones = Enumerable.Range(0, Math.Max(0, town.GetInt("zones_count")))
                .Where(i => town.Ints.ContainsKey($"zone_x_{i}") && town.Ints.ContainsKey($"zone_y_{i}"))
                .Select(i => new ZoneCoordinate(town.GetInt($"zone_x_{i}"), town.GetInt($"zone_y_{i}")));
            if (!result.TryGetValue(instance, out var list)) result[instance] = list = [];
            list.AddRange(zones.Where(z => !list.Contains(z)));
        }

        return result;
    }

    /// <summary>
    /// Road segments: <see cref="FcsRecordType.Road"/> records whose instances are the path's points.
    /// </summary>
    public IEnumerable<RoadPath> Roads() =>
        Level.OfType(FcsRecordType.Road).Select(RoadOf);

    /// <summary>
    /// The road of a <see cref="FcsRecordType.Road"/> record.
    /// </summary>
    static RoadPath RoadOf(GameRecord road) =>
        new(road.StringId, road.GetFloat("width"), road.GetInt("start"), road.GetInt("end"), road.GetFloat("length"), RoadPoints(road));

    /// <summary>
    /// A road's points in the original's order (docs/formats/zones.md, <b>Verified</b>, the road loader <c>FUN_14045e760</c>): the instance ids
    /// are the points' numbers in lowercase hex without padding, looked up from 0 on until one is missing or the point read has a rotation x of
    /// 1 or more, the road's last point. The level files list the ids as text (<c>1</c>, <c>10</c> ... <c>1f</c>, <c>2</c>), so file order
    /// scrambles a road of over 16 points. A point a later layer cleared is left out and the road goes on (engine choice; no base road has one).
    /// </summary>
    static List<Vector3> RoadPoints(GameRecord road)
    {
        var points = new List<Vector3>();
        for (int i = 0; road.Instances.TryGetValue(i.ToString("x"), out var point); i++)
        {
            if (point.IsCleared) continue;

            points.Add(point.Position);
            if (point.Rotation.X >= 1) break;
        }

        return points;
    }

    #endregion
}

/// <summary>
/// What the level files of one zone hold besides records (<see cref="WorldLevelData.ZoneInfo"/>).
/// </summary>
public sealed class ZoneLevelInfo
{
    /// <summary>
    /// The last layer's slot list (the trailer; a layer without one empties it): the handle slots (<c>I</c>) of the zone container in use.
    /// </summary>
    public IReadOnlyList<int> Slots { get; internal set; } = [];

    /// <summary>
    /// The highest <c>nextId</c> of the layers' headers.
    /// </summary>
    public int NextId { get; internal set; }
}

/// <summary>
/// One folder of level files.
/// </summary>
public sealed class WorldLevelLayer(string name, string directory)
{
    /// <summary>
    /// Game-data file name without extension, or "" for the base folder.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// The folder's path.
    /// </summary>
    public string Directory { get; } = directory;
    /// <summary>
    /// How many level files the folder gave.
    /// </summary>
    public int FileCount { get; internal set; }

    /// <summary>
    /// The folder's zone files (<c>zone.X.Y.zone</c>), in ordinal order.
    /// </summary>
    public IEnumerable<string> ZoneFiles() => System.IO.Directory.EnumerateFiles(Directory, "zone.*.zone").Order(StringComparer.Ordinal);

    /// <summary>
    /// Every file of the folder the loader reads: <see cref="ZoneFiles"/>, then the level and interiors files (named whether or not they exist).
    /// </summary>
    public IEnumerable<string> Files() =>
    [
        .. ZoneFiles(),
        GameFiles.Resolve(Path.Combine(Directory, WorldLevelData.LevelFileName)),
        GameFiles.Resolve(Path.Combine(Directory, WorldLevelData.InteriorsFileName)),
    ];

    public override string ToString() => Name.Length == 0 ? "(base)" : Name;
}

/// <param name="InstanceId">The id in its zone file's building list; ids repeat between zones (Observed: 8,754 of 11,714 base placements share
/// one), so at run time a building is named by <see cref="Key"/>.</param>
/// <param name="Position">X and Z are world coordinates; Y is the height above the terrain (Observed).</param>
/// <param name="BuildingId">String id of the placed <see cref="FcsRecordType.Building"/> record.</param>
/// <param name="StateId">String id of its <see cref="FcsRecordType.GamestateBuilding"/> record in the same zone file.</param>
/// <param name="WorldY">Absolute height: for an outdoor level building the original's building ground + <c>Position.Y</c>
/// (<see cref="WorldLevelData.PlaceOnGround"/>), else its state's <c>world Y pos</c>.</param>
public sealed record BuildingPlacement(
    ZoneCoordinate Zone,
    string InstanceId,
    string BuildingId,
    Vector3 Position,
    Quaternion Rotation,
    string? StateId,
    float? WorldY)
{
    /// <summary>
    /// The <see cref="Key"/> given at init, or null for the zone and instance id's.
    /// </summary>
    readonly string? _key;

    /// <summary>
    /// Runtime repair state; null leaves the level/save flag in control.
    /// </summary>
    public bool? DestroyedOverride { get; init; }

    /// <summary>
    /// Its state's interior layout is not laid out: a town house the player bought, whose furniture the purchase destroyed (docs/simulation.md "Town
    /// house purchases").
    /// </summary>
    public bool WithoutInterior { get; init; }

    /// <summary>
    /// The player's own building (a site the player built or repairs): only such a WALKABLE linked wall gets seeds on its top
    /// (docs/game/pathfinding.md "Generator", seeds).
    /// </summary>
    public bool PlayerOwned { get; init; }

    /// <summary>
    /// Its interior and exterior layout names when not its state's: a town override's or a loaded save's residents named them
    /// (<see cref="TownBuildings.Relay"/>, docs/game/factions-squads-towns.md "Town residents"); null leaves the state's.
    /// </summary>
    public string? InteriorLayout { get; init; }

    /// <summary>
    /// See <see cref="InteriorLayout"/>.
    /// </summary>
    public string? ExteriorLayout { get; init; }

    /// <summary>
    /// The name of its interior or exterior layout: its own (<see cref="InteriorLayout"/>), else its state's; "" for none.
    /// </summary>
    public string LayoutName(GameRecord? state, bool exterior) =>
        (exterior ? ExteriorLayout : InteriorLayout) ?? state?.GetString(exterior ? ResidentRuins.ExteriorKey : ResidentRuins.InteriorKey) ?? "";

    /// <summary>
    /// A loose weapon's model (its instance's <c>material sid</c>, a MATERIAL_SPECS_WEAPON): its look instead of the resolver's pick; null or empty
    /// for none.
    /// </summary>
    public string? ItemMaterial { get; init; }

    /// <summary>
    /// A loose item's mesh and scale when not its record's own (a nest's numbered debris piece, docs/game/factions-squads-towns.md "Nest furniture").
    /// </summary>
    public string? ItemMesh { get; init; }

    /// <summary>
    /// See <see cref="ItemMesh"/>.
    /// </summary>
    public float ItemScale { get; init; } = 1;

    /// <summary>
    /// The run-time name of the building (<see cref="BuildingKey.Of"/>); a player-placed building, whose id is unique already, is given its own.
    /// </summary>
    public string Key { get => _key ?? BuildingKey.Of(Zone, InstanceId); init => _key = value; }

    /// <summary>
    /// True when <see cref="BuildingId"/> names a record of the zone file itself rather than a building.
    /// </summary>
    public bool TargetsZoneRecord { get; init; }

    /// <summary>
    /// Placed inside another building and not outside (<see cref="PlacedMesh.Contained"/>); a zone's own buildings take it from their state instead.
    /// </summary>
    public bool Contained { get; init; }

    /// <summary>
    /// The building it was placed in (<see cref="PlacedMesh.ContainerId"/>), when known.
    /// </summary>
    public string? ContainerId { get; init; }

    /// <summary>
    /// Laid out the same wherever it stands: no town's material, its parts' variants chosen as at a fixed place (the character editor's bar).
    /// </summary>
    public bool Standalone { get; init; }

    /// <summary>
    /// The zone file holding <see cref="StateId"/> when it is not <see cref="Zone"/>: a copy of a level building placed elsewhere (the Meitou
    /// editor's bar) keeps its state and layouts.
    /// </summary>
    public ZoneCoordinate? StateZone { get; init; }
}

/// <param name="TownId">String id of the placed <see cref="FcsRecordType.Town"/> record.</param>
/// <param name="Position">World position; Y is an absolute height.</param>
public sealed record TownPlacement(string InstanceId, string TownId, Vector3 Position, string? StateId);

/// <param name="Start">The road's <c>start</c> junction number (roads sharing a number meet there).</param>
/// <param name="End">Its <c>end</c> junction number.</param>
/// <param name="Length">Its <c>length</c> field: what the original's road search counts it as.</param>
/// <param name="Points">World positions of the road's points in instance order; Y is absolute.</param>
public sealed record RoadPath(string StringId, float Width, int Start, int End, float Length, List<Vector3> Points);
