using System.Numerics;
using Meitou.Content;
using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>
/// The placed contents of the world from <c>data/newland/leveldata/</c> (docs/formats/zones.md): zone files
/// (buildings), <c>leveldata.level</c> (towns, roads) and optionally <c>interiors.level</c>. Files come in
/// layers: the folder itself, then one sub-folder per game-data file of the load order that has one (base
/// game: <c>Newwworld</c>, <c>Dialogue</c>, <c>rebirth</c>). Layers are merged per file with the FCS rules of
/// <see cref="GameDatabase"/>; that the game merges them this way is an assumption.
/// </summary>
public sealed class WorldLevelData
{
    /// <summary>Folder of the world's level data, relative to the install's <c>data/</c> folder.</summary>
    public const string RelativeDirectory = "newland/leveldata";

    public const string LevelFileName = "leveldata.level";
    public const string InteriorsFileName = "interiors.level";

    /// <summary>Record holding the placed buildings of a zone file.</summary>
    public const string BuildingListId = "0-buildinglist";

    /// <summary>Layers in the order they were applied.</summary>
    public List<WorldLevelLayer> Layers { get; } = [];

    /// <summary>Merged zone files by grid cell.</summary>
    public SortedDictionary<ZoneCoordinate, GameDatabase> Zones { get; } = new(Comparer<ZoneCoordinate>.Create((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X)));

    /// <summary>Merged <c>leveldata.level</c> files: town list, town states, roads.</summary>
    public GameDatabase Level { get; } = new();

    /// <summary>Merged <c>interiors.level</c> files, or null when not loaded.</summary>
    public GameDatabase? Interiors { get; private set; }

    /// <summary>
    /// Per zone, what a save's zone file carries over from the level files besides the merged records (docs/formats/save.md "Zone files"): the last layer's slot list
    /// (the trailer) and the highest <c>nextId</c> of the layers.
    /// </summary>
    public Dictionary<ZoneCoordinate, ZoneLevelInfo> ZoneInfo { get; } = [];

    /// <summary>Merge problems over all files (records defined twice, ...), tagged with the layer file.</summary>
    public List<GameDataIssue> Issues { get; } = [];

    public static WorldLevelData Load(GameInstall install, bool includeInteriors = false) =>
        Load(Path.Combine(install.DataDirectory, RelativeDirectory), LoadOrder.FromInstall(install).Entries.Select(e => e.Name), includeInteriors);

    /// <summary>Loads from <paramref name="directory"/>, with sub-folder layers named after <paramref name="loadOrder"/> files (extension dropped).</summary>
    public static WorldLevelData Load(string directory, IEnumerable<string> loadOrder, bool includeInteriors = false)
    {
        var world = new WorldLevelData();
        world.Layers.Add(new WorldLevelLayer("", directory));
        foreach (var name in loadOrder)
        {
            var sub = Path.Combine(directory, Path.GetFileNameWithoutExtension(name));
            if (Directory.Exists(sub))
                world.Layers.Add(new WorldLevelLayer(Path.GetFileNameWithoutExtension(name), sub));
        }
        if (includeInteriors) world.Interiors = new GameDatabase();

        foreach (var layer in world.Layers)
        {
            foreach (var path in Directory.EnumerateFiles(layer.Directory, "zone.*.zone").Order(StringComparer.Ordinal))
            {
                if (!ZoneCoordinate.TryParseFileName(path, out var zone)) continue;
                if (!world.Zones.TryGetValue(zone, out var db))
                    world.Zones[zone] = db = new GameDatabase();
                world.Apply(db, path, layer, zone);
            }
            var level = GameFiles.Resolve(Path.Combine(layer.Directory, LevelFileName));
            if (File.Exists(level)) world.Apply(world.Level, level, layer);
            var interiors = GameFiles.Resolve(Path.Combine(layer.Directory, InteriorsFileName));
            if (world.Interiors is not null && File.Exists(interiors)) world.Apply(world.Interiors, interiors, layer);
        }
        return world;
    }

    void Apply(GameDatabase db, string path, WorldLevelLayer layer, ZoneCoordinate? zone = null)
    {
        var name = layer.Name.Length == 0 ? Path.GetFileName(path) : $"{layer.Name}/{Path.GetFileName(path)}";
        int before = db.Issues.Count;
        var file = LevelFile.ReadFile(path);
        if (zone is { } z)
        {
            if (!ZoneInfo.TryGetValue(z, out var info)) ZoneInfo[z] = info = new ZoneLevelInfo();
            // A later layer's slot list replaces an earlier one's, as in the game's container (Observed: a save's list extends the last layer's, not the union).
            info.Slots = file.Trailer is { } slots && file.TrailerEntryInts == 1 ? slots : [];
            info.NextId = Math.Max(info.NextId, file.Data.NextId);
        }
        db.Apply(file.Data, name);
        Issues.AddRange(db.Issues.Skip(before));
        layer.FileCount++;
    }

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
                string? stateId = instance.States.Count > 0 ? instance.States[0] : null;
                float? worldY = stateId is not null && db.Find(stateId) is { } state && state.Floats.TryGetValue("world Y pos", out var y) ? y : null;
                yield return new BuildingPlacement(zone, instance.Id, instance.Target, instance.Position, instance.Rotation, stateId, worldY)
                {
                    TargetsZoneRecord = db.Find(instance.Target) is not null,
                };
            }
        }
    }

    /// <summary>Every town placed by a <see cref="FcsRecordType.GAMESTATE_TOWN_INSTANCE_LIST"/> record of the level files.</summary>
    public IEnumerable<TownPlacement> Towns()
    {
        foreach (var list in Level.OfType(FcsRecordType.GAMESTATE_TOWN_INSTANCE_LIST))
            foreach (var instance in list.Instances.Values.Where(i => !i.IsCleared))
                yield return new TownPlacement(instance.Id, instance.Target, instance.Position, instance.States.Count > 0 ? instance.States[0] : null);
    }

    /// <summary>
    /// Zones listed by <see cref="FcsRecordType.GAMESTATE_TOWN"/> records (<c>zones_count</c>, <c>zone_x_i</c>,
    /// <c>zone_y_i</c>), keyed by their <c>instance</c> field: the id of the town's placement.
    /// </summary>
    public Dictionary<string, List<ZoneCoordinate>> TownZones()
    {
        var result = new Dictionary<string, List<ZoneCoordinate>>(StringComparer.Ordinal);
        foreach (var town in Level.OfType(FcsRecordType.GAMESTATE_TOWN))
        {
            var instance = town.GetString("instance");
            if (instance.Length == 0) continue;
            var zones = Enumerable.Range(0, town.GetInt("zones_count"))
                .Select(i => new ZoneCoordinate(town.GetInt($"zone_x_{i}"), town.GetInt($"zone_y_{i}")));
            if (!result.TryGetValue(instance, out var list)) result[instance] = list = [];
            list.AddRange(zones.Where(z => !list.Contains(z)));
        }
        return result;
    }

    /// <summary>Road segments: <see cref="FcsRecordType.ROAD"/> records whose instances are the path's points.</summary>
    public IEnumerable<RoadPath> Roads() =>
        Level.OfType(FcsRecordType.ROAD).Select(r => new RoadPath(r.StringId, r.GetFloat("width"), r.GetInt("start"), r.GetInt("end"), r.GetFloat("length"),
            r.Instances.Values.Where(i => !i.IsCleared).Select(i => i.Position).ToList()));
}

/// <summary>What the level files of one zone hold besides records (<see cref="WorldLevelData.ZoneInfo"/>).</summary>
public sealed class ZoneLevelInfo
{
    /// <summary>The last layer's slot list (the trailer; a layer without one empties it): the handle slots (<c>I</c>) of the zone container in use.</summary>
    public IReadOnlyList<int> Slots { get; internal set; } = [];
    /// <summary>The highest <c>nextId</c> of the layers' headers.</summary>
    public int NextId { get; internal set; }
}

/// <summary>One folder of level files.</summary>
public sealed class WorldLevelLayer(string name, string directory)
{
    /// <summary>Game-data file name without extension, or "" for the base folder.</summary>
    public string Name { get; } = name;
    public string Directory { get; } = directory;
    public int FileCount { get; internal set; }
    public override string ToString() => Name.Length == 0 ? "(base)" : Name;
}

/// <param name="InstanceId">The id in its zone file's building list; ids repeat between zones (Observed: 8,754 of 11,714 base placements share one), so at run time a
/// building is named by <see cref="Key"/>.</param>
/// <param name="Position">X and Z are world coordinates; Y is the height above the terrain (Observed).</param>
/// <param name="BuildingId">String id of the placed <see cref="FcsRecordType.BUILDING"/> record.</param>
/// <param name="StateId">String id of its <see cref="FcsRecordType.GAMESTATE_BUILDING"/> record in the same zone file.</param>
/// <param name="WorldY">The state's <c>world Y pos</c>: absolute height (terrain height + <c>Position.Y</c> for most).</param>
public sealed record BuildingPlacement(ZoneCoordinate Zone, string InstanceId, string BuildingId, Vector3 Position, Quaternion Rotation, string? StateId, float? WorldY)
{
    readonly string? key;

    /// <summary>The run-time name of the building (<see cref="BuildingKey.Of"/>); a player-placed building, whose id is unique already, is given its own.</summary>
    public string Key { get => key ?? BuildingKey.Of(Zone, InstanceId); init => key = value; }
    /// <summary>True when <see cref="BuildingId"/> names a record of the zone file itself rather than a building.</summary>
    public bool TargetsZoneRecord { get; init; }
    /// <summary>Placed inside another building and not outside (<see cref="PlacedMesh.Contained"/>); a zone's own buildings take it from their state instead.</summary>
    public bool Contained { get; init; }
}

/// <param name="TownId">String id of the placed <see cref="FcsRecordType.TOWN"/> record.</param>
/// <param name="Position">World position; Y is an absolute height.</param>
public sealed record TownPlacement(string InstanceId, string TownId, Vector3 Position, string? StateId);

/// <param name="Start">The road's <c>start</c> junction number (roads sharing a number meet there).</param>
/// <param name="End">Its <c>end</c> junction number.</param>
/// <param name="Length">Its <c>length</c> field: what the original's road search counts it as.</param>
/// <param name="Points">World positions of the road's points in instance order; Y is absolute.</param>
public sealed record RoadPath(string StringId, float Width, int Start, int End, float Length, List<Vector3> Points);
