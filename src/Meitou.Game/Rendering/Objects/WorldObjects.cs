using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Objects;

/// <summary>What <see cref="WorldObjects.BuildZone"/> made of one zone.</summary>
sealed class ZoneObjects
{
    public required ZoneCoordinate Zone { get; init; }
    /// <summary>Building parts and map features.</summary>
    public List<PlacedMesh> Items { get; } = [];
    /// <summary>Distant meshes of buildings in towns that have no baked distant town mesh (<see cref="PlacedKind.BuildingDistant"/>).</summary>
    public List<PlacedMesh> Stand { get; } = [];
    /// <summary>The indoor volumes of the zone's buildings that have an <c>interior mask</c> (docs/formats/zones.md, "Interior view").</summary>
    public List<InteriorVolume> Interiors { get; } = [];
    /// <summary>The hulls of gateway buildings: hit by the audio's indoor ray like any interior mask, but no room (<see cref="InteriorVolume.IsGateway"/>).</summary>
    public List<InteriorVolume> GateHulls { get; } = [];
}

/// <summary>
/// The buildings (zone files) and map features (<c>features.dat</c>) of the whole world, ready to be laid out one zone at a time
/// (<see cref="BuildZone"/>, safe to call from worker threads) as the camera moves, and the towns that have a baked distant mesh.
/// </summary>
sealed class WorldObjects : IDisposable
{
    public required GameDatabase Database { get; init; }
    public required GameInstall Install { get; init; }
    public required WorldLevelData Levels { get; init; }
    public required BuildingTowns Towns { get; init; }
    public required MapFeatureFile Features { get; init; }
    /// <summary>Building layouts of <c>interiors.level</c>; the exterior ones (signs, banners) are drawn (docs/formats/zones.md, "Building layouts").</summary>
    public required BuildingLayouts Layouts { get; init; }
    /// <summary>Towns with a baked distant mesh (docs/formats/zones.md, "Distant towns").</summary>
    public required List<DistantTown> DistantTowns { get; init; }

    readonly HashSet<string> _bakedTowns = new(StringComparer.Ordinal);
    readonly Dictionary<ZoneCoordinate, List<BuildingPlacement>> _buildingsByZone = [];
    Dictionary<ZoneCoordinate, ZoneObjects>? _staged;
    ThreadLocal<TerrainHeightmap>? _heights;
    InteriorCollisions? _interiorCollisions;

    // Totals over the zones laid out so far (for the log line).
    int _buildings, _missing, _fromState, _destroyed, _foliage, _empty, _features, _stand, _layouts, _layoutObjects, _layoutsNotFound;

    /// <summary>Zones that have placed buildings or features.</summary>
    public IEnumerable<ZoneCoordinate> PopulatedZones => _staged?.Keys ?? _buildingsByZone.Keys.Concat(
        Features.Zones.Select((l, i) => (l, i)).Where(t => t.l.Count > 0).Select(t => MapFeatureFile.ZoneOf(t.i))).Distinct();

    /// <summary>Renderer-only placements, with no changes to the world's records or simulation.</summary>
    public static WorldObjects Stage(GameInstall install, GameDatabase database, IEnumerable<PlacedMesh> meshes)
    {
        var levels = new WorldLevelData();
        var result = new WorldObjects
        {
            Install = install,
            Database = database,
            Levels = levels,
            Towns = new(database, levels),
            Features = new(),
            Layouts = new(database, null),
            DistantTowns = [],
            _staged = [],
        };
        foreach (var mesh in meshes)
        {
            var zone = WorldLayout.ZoneOf(mesh.Transform.M41, mesh.Transform.M43);
            if (!result._staged.TryGetValue(zone, out var group)) result._staged[zone] = group = new() { Zone = zone };
            group.Items.Add(mesh);
        }
        return result;
    }

    /// <summary>
    /// Reads the world's placement data once. The region arguments are not used: objects are laid out per zone around the camera
    /// by <see cref="BuildZone"/>.
    /// </summary>
    public static WorldObjects Load(GameInstall install, GameDatabase db, TerrainHeightmap map, double x0, double z0, double x1, double z1)
    {
        var watch = Stopwatch.StartNew();
        var levels = WorldLevelData.Load(install, includeInteriors: true);
        WorldFrame.LogIssues("world levels", levels.Problems());

        var objects = new WorldObjects
        {
            Database = db,
            Install = install,
            Levels = levels,
            Towns = new BuildingTowns(db, levels),
            Features = MapFeatureFile.Open(install),
            Layouts = new BuildingLayouts(db, levels.Interiors),
            DistantTowns = Meitou.Data.World.DistantTowns.Find(install, db, levels),
        };
        // The factions' building swaps, once, for every consumer of the placements and of their layouts' furniture.
        int swapped = levels.ApplyBuildingSwaps(db, objects.Layouts);
        if (swapped > 0) Console.WriteLine($"world     {swapped} building(s) replaced or skipped by the factions' swaps");
        foreach (var t in objects.DistantTowns) objects._bakedTowns.Add(t.InstanceId);
        int ruins = ResidentRuins.Apply(db, levels, objects.Layouts);
        foreach (var b in levels.Buildings())
        {
            if (!objects._buildingsByZone.TryGetValue(b.Zone, out var list)) objects._buildingsByZone[b.Zone] = list = [];
            list.Add(b);
        }
        objects._interiorCollisions = new InteriorCollisions(install);
        objects._heights = new ThreadLocal<TerrainHeightmap>(() => TerrainHeightmap.Open(install), trackAllValues: true);
        Console.WriteLine($"objects   {objects._buildingsByZone.Values.Sum(l => l.Count)} placed buildings and {objects.Features.All().Count()} map features in {objects.PopulatedZones.Count()} zones, " +
            $"{objects.DistantTowns.Count} towns with a distant mesh, {objects.Layouts.Count} building layouts, {ruins} ruined by their residents ({watch.ElapsedMilliseconds} ms)");
        return objects;
    }

    // ---- runtime placements (buildings the player placed: docs/simulation.md "Buildings", host seam) ----

    readonly object _runtimeLock = new();
    readonly Dictionary<string, BuildingPlacement> _runtime = new(StringComparer.Ordinal);
    // Replaced whole on every change, so worker threads in BuildZone read a stable snapshot without the lock.
    volatile Dictionary<ZoneCoordinate, BuildingPlacement[]> _runtimeByZone = [];

    /// <summary>Adds (or replaces, by <see cref="BuildingPlacement.Key"/>) a runtime placement or town-house override; zones laid out from now on include it.</summary>
    public void AddRuntime(BuildingPlacement placement)
    {
        lock (_runtimeLock)
        {
            _runtime[placement.Key] = placement;
            Reindex(placement.Key);
        }
    }

    /// <summary>Removes a runtime placement; returns it (null when there was none).</summary>
    public BuildingPlacement? RemoveRuntime(string instanceId)
    {
        lock (_runtimeLock)
        {
            if (!_runtime.Remove(instanceId, out var removed)) return null;
            Reindex(instanceId);
            return removed;
        }
    }

    /// <summary>Drops every runtime placement (a loaded save replaces the world); returns their zones.</summary>
    public List<ZoneCoordinate> ClearRuntime()
    {
        lock (_runtimeLock)
        {
            var zones = _runtime.Values.Select(p => p.Zone).Distinct().ToList();
            _runtime.Clear();
            Reindex();
            return zones;
        }
    }

    /// <summary>Whether a runtime placement with this id is still in the store.</summary>
    public bool HasRuntime(string instanceId) { lock (_runtimeLock) return _runtime.ContainsKey(instanceId); }

    /// <summary>The key prefix of a ground item's placement: its comings and goings leave the building floors alone (a pick-up inside a building must not drop the camera to the terrain).</summary>
    public const string GroundKeyPrefix = "ground:";

    void Reindex(string? key = null)
    {
        _runtimeByZone = _runtime.Values.GroupBy(p => p.Zone).ToDictionary(g => g.Key, g => g.ToArray());
        if (key is null || !key.StartsWith(GroundKeyPrefix, StringComparison.Ordinal)) _placementVersion++;
    }

    IReadOnlyList<BuildingPlacement> RuntimeIn(ZoneCoordinate zone) => _runtimeByZone.TryGetValue(zone, out var list) ? list : [];

    // Level buildings the level editor cleared (docs/game/level-editor.md), by key; replaced whole on every change, like runtimeByZone.
    volatile HashSet<string> _cleared = new(StringComparer.Ordinal);

    /// <summary>Takes a level-data building out of the zones laid out from now on (<paramref name="on"/> false puts it back); true when that changed anything.</summary>
    public bool SetCleared(string key, bool on)
    {
        lock (_runtimeLock)
        {
            if (_cleared.Contains(key) == on) return false;
            var next = new HashSet<string>(_cleared, StringComparer.Ordinal);
            if (on) next.Add(key); else next.Remove(key);
            _cleared = next;
            _placementVersion++;
            return true;
        }
    }

    /// <summary>The level-data buildings of a zone that are not cleared.</summary>
    IEnumerable<BuildingPlacement> FixedIn(ZoneCoordinate zone)
    {
        var list = _buildingsByZone.GetValueOrDefault(zone);
        if (list is null) return [];
        var gone = _cleared;
        return gone.Count == 0 ? list : list.Where(b => !gone.Contains(b.Key));
    }

    volatile int _placementVersion;
    BuildingFloors? _floors;

    /// <summary>Changes whenever a runtime placement other than a ground item is added or removed: what <see cref="Floors"/> listed before is stale.</summary>
    internal int PlacementVersion => _placementVersion;

    /// <summary>The building floors under the characters' feet (the footstep surface, docs/audio.md "Surface") and under the camera; used from the main thread.</summary>
    internal BuildingFloors Floors => _floors ??= new BuildingFloors(this);

    /// <summary>
    /// The floors of the buildings placed in a zone that characters can stand on: not destroyed ones, nor those whose path mode ignores them or that are furniture (the
    /// parts the game's ground ray has no group for), with their ground materials. The floor collision itself is made later, per building (<see cref="BuildingFloor.Make"/>).
    /// Safe from a worker thread.
    /// </summary>
    internal BuildingFloor[] FloorsOf(ZoneCoordinate zone)
    {
        if (_staged is not null || _heights?.Value is not { } map || _interiorCollisions is not { } collisions) return [];
        var result = new List<BuildingFloor>();
        foreach (var placement in BuildingPlacements.Overlay(FixedIn(zone), RuntimeIn(zone)))
        {
            if (BuildingPlacements.Resolve(placement, Database, Levels, map.HeightAt) is not { Destroyed: false } resolved || resolved.Record.Type != Data.Fcs.FcsRecordType.Building) continue;
            var (b, record, position) = (placement, resolved.Record, resolved.Position);
            if (record.GetInt("path mode", 2) == 0 || record.GetBool("is exterior furniture") || record.GetBool("is interior furniture")) continue;
            float scale = record.GetFloat("scale", 1);
            var node = WorldObjectLayout.InstanceTransform(position, b.Rotation, new Vector3(scale > 0 ? scale : 1));
            result.Add(new BuildingFloor
            {
                Key = b.Key,
                Placement = b,
                Position = position,
                Interior = record.GetInt("interior material", 3),
                Exterior = record.GetInt("exterior material", 2),
                Make = () => FloorSurfaces.For(Database, record, b.Key, position, b.Rotation, node, collisions),
            });
        }
        return [.. result];
    }

    /// <summary>
    /// The sound emitters of the level's buildings (docs/audio.md "Point emitters"): those whose record has <c>sounds</c>, that are not destroyed and whose state does not say
    /// <c>is complete</c> is false (a state without the key counts as complete), and those of the furniture in the state's interior and exterior layouts (ovens, stoves).
    /// Positioned as <see cref="Lay"/> places the building and its layout objects. Safe from a worker thread.
    /// </summary>
    public List<PlacedEmitter> Emitters()
    {
        var result = new List<PlacedEmitter>();
        using var map = TerrainHeightmap.Open(Install);
        foreach (var b in Levels.Buildings())
        {
            if (b.Position == Vector3.Zero || Database.Find(b.BuildingId) is not { } record) continue;
            var state = b.StateId is not null && Levels.Zones.TryGetValue(b.StateZone ?? b.Zone, out var zoneDb) ? zoneDb.Find(b.StateId) : null;
            if (state is not null && state.GetBool("destroyed")) continue;
            bool incomplete = state is not null && state.Bools.TryGetValue("is complete", out bool complete) && !complete;
            bool sounds = !incomplete && record.GetReferences("sounds").Count > 0;
            var layouts = new List<GameRecord>(2);
            foreach (bool exterior in (ReadOnlySpan<bool>)[true, false])
                if (b.LayoutName(state, exterior) is { Length: > 0 } layoutName && Layouts.Find(record, layoutName, exterior) is { } layout)
                    layouts.Add(layout);
            if (!sounds && layouts.Count == 0) continue;
            float y = b.WorldY ?? map.HeightAt(b.Position.X, b.Position.Z) + b.Position.Y;
            var position = new Vector3(b.Position.X, y, b.Position.Z);
            string key = b.Key;
            if (sounds && BuildingEmitters.For(Database, record, key, position, b.Rotation) is { } emitter) result.Add(emitter);
            foreach (var layout in layouts)
                foreach (var item in Layouts.Items(layout, b))
                {
                    if (item.Building.GetReferences("sounds").Count == 0) continue;
                    var (at, rotation) = BuildingLayouts.Place(position, b.Rotation, item);
                    if (BuildingEmitters.For(Database, item.Building, $"{key}/{item.InstanceId}", at, rotation) is { } furniture) result.Add(furniture);
                }
        }
        return result;
    }

    /// <summary>
    /// The level's objects with <c>bird attractor</c> references (docs/game/birds.md): the buildings of the zone files that are not flagged incomplete by their state, the furniture
    /// of their layouts, and the map features. Positioned as <see cref="Lay"/> places them. Safe from a worker thread.
    /// </summary>
    public List<(string Key, GameRecord Record, Vector3 Position)> BirdAttractorSources()
    {
        var result = new List<(string, GameRecord, Vector3)>();
        using var map = TerrainHeightmap.Open(Install);
        foreach (var b in Levels.Buildings())
        {
            if (b.Position == Vector3.Zero || Database.Find(b.BuildingId) is not { } record) continue;
            var state = b.StateId is not null && Levels.Zones.TryGetValue(b.StateZone ?? b.Zone, out var zoneDb) ? zoneDb.Find(b.StateId) : null;
            if (state is not null && state.Bools.TryGetValue("is complete", out bool complete) && !complete) continue;
            float y = b.WorldY ?? map.HeightAt(b.Position.X, b.Position.Z) + b.Position.Y;
            var position = new Vector3(b.Position.X, y, b.Position.Z);
            if (record.GetReferences("bird attractor").Count > 0) result.Add((b.Key, record, position));
            foreach (bool exterior in (ReadOnlySpan<bool>)[true, false])
                if (state?.GetString(exterior ? "exterior layout name" : "interior layout name") is { Length: > 0 } name && Layouts.Find(record, name, exterior) is { } layout)
                    foreach (var item in Layouts.Items(layout, b))
                        if (item.Building.GetReferences("bird attractor").Count > 0)
                            result.Add(($"{b.Key}/{item.InstanceId}", item.Building, BuildingLayouts.Place(position, b.Rotation, item).Position));
        }
        foreach (var (zone, feature) in Features.All())
            if (Database.Find(feature.StringId) is { Type: Data.Fcs.FcsRecordType.MapFeatures } record && record.GetReferences("bird attractor").Count > 0)
                result.Add(($"feature {zone} {feature.Position}", record, feature.Position));
        return result;
    }

    /// <summary>Lays out one zone's buildings and map features as the game builds them (docs/formats/zones.md).</summary>
    public ZoneObjects BuildZone(ZoneCoordinate zone)
    {
        if (_staged is not null) return _staged.GetValueOrDefault(zone) ?? new() { Zone = zone };
        return Lay(zone, BuildingPlacements.Overlay(FixedIn(zone), RuntimeIn(zone)), withFeatures: true);
    }

    /// <summary>Lays out one runtime placement alone (a building the player placed), for a zone that is already laid out (worker threads).</summary>
    public ZoneObjects BuildRuntime(BuildingPlacement placement) => Lay(placement.Zone, [placement], withFeatures: false);

    ZoneObjects Lay(ZoneCoordinate zone, IEnumerable<BuildingPlacement> placements, bool withFeatures)
    {
        var result = new ZoneObjects { Zone = zone };
        var map = _heights!.Value!;
        var db = Database;
        {
            foreach (var b in placements)
            {
                if (b.Position == Vector3.Zero) continue;
                if (db.Find(b.BuildingId) is not { } record) { Interlocked.Increment(ref _missing); continue; }
                if (record.Type != Data.Fcs.FcsRecordType.Building)
                {
                    // A loose item (GroundItemSync): its one mesh, at its absolute height.
                    var at = new Vector3(b.Position.X, b.WorldY ?? map.HeightAt(b.Position.X, b.Position.Z) + b.Position.Y, b.Position.Z);
                    if (WorldObjectLayout.Item(record, b.InstanceId, at, b.Rotation, b.ItemMesh, b.ItemScale) is { } item)
                        result.Items.Add(item);
                    continue;
                }
                // Y: the placement's absolute height when it has one (the building ground + instance Y, or a state's "world Y pos"), else terrain height + the instance's Y (zones.md).
                float y = b.WorldY ?? map.HeightAt(b.Position.X, b.Position.Z) + b.Position.Y;
                if (b.WorldY is not null) Interlocked.Increment(ref _fromState);
                var state = b.StateId is not null && Levels.Zones.TryGetValue(b.StateZone ?? b.Zone, out var zoneDb) ? zoneDb.Find(b.StateId) : null;
                bool isDestroyed = b.DestroyedOverride ?? state?.GetBool("destroyed") ?? false;
                if (isDestroyed) Interlocked.Increment(ref _destroyed);
                var town = Towns.TownOf(state, b.Position);
                GameRecord? townMaterial;
                lock (Towns) townMaterial = Towns.MaterialOf(town);
                var position = new Vector3(b.Position.X, y, b.Position.Z);
                var parts = WorldObjectLayout.Building(db, record, b.InstanceId, position, b.Rotation, new BuildingState(isDestroyed, townMaterial) { WithInterior = !isDestroyed, Identity = b.Key });
                // A destroyed building gets no interior view (game: the per-frame interior driver skips it).
                if (!isDestroyed && InteriorVolume.For(db, record, b.Key, position, b.Rotation, _interiorCollisions!, gateways: true) is { } interior) (interior.IsGateway ? result.GateHulls : result.Interiors).Add(interior);
                // Resource buildings ("is foliage", no parts) are drawn by the foliage system as a FOLIAGE_MESH naming them as "building type".
                if (parts.Count == 0 && record.GetBool("is foliage")) Interlocked.Increment(ref _foliage);
                else if (parts.Count == 0) Interlocked.Increment(ref _empty);
                parts.RemoveAll(m => m.RenderHidden);   // collision only (a destroyed building's upper-floor part with no destroyed mesh)
                // Inside another building and not outside: hidden while that one is closed (a player's own building says so; a zone's by its state).
                if (b.Contained || state is not null && state.GetBool("is inside building") && !BuildingLayouts.IsOutside(state))
                    for (int k = 0; k < parts.Count; k++) parts[k] = parts[k] with { Contained = true, ContainerId = b.ContainerId };
                result.Items.AddRange(parts);
                Interlocked.Increment(ref _buildings);
                // The state's exterior layout (signs and banners) and interior layout (furniture), placed relative to the building, each object on its
                // layout floor. An interior layout's object shows only in the interior view unless its state is flagged outside (game: the closed interior hides
                // its contained objects but those; "is exterior furniture" plays no part). Skipped for a ruin, which holds no furniture
                // (docs/game/buildings-production.md "Ruined by residents"); the interior one also for a bought town house, whose furniture the purchase
                // destroyed (BuildingPlacement.WithoutInterior).
                foreach (bool exterior in (ReadOnlySpan<bool>)[true, false])
                {
                    if (isDestroyed || !exterior && b.WithoutInterior) continue;
                    if (b.LayoutName(state, exterior) is not { Length: > 0 } layoutName) continue;
                    if (Layouts.Find(record, layoutName, exterior) is not { } layout) { Interlocked.Increment(ref _layoutsNotFound); continue; }
                    Interlocked.Increment(ref _layouts);
                    foreach (var item in Layouts.Items(layout, b))
                    {
                        var (at, rotation) = BuildingLayouts.Place(position, b.Rotation, item);
                        var placed = WorldObjectLayout.Building(db, item.Building, $"{b.InstanceId}/{item.InstanceId}", at, rotation,
                            new BuildingState(false, townMaterial) { Identity = $"{b.Key}/{item.InstanceId}" });
                        bool insideOnly = !exterior && !item.Outside;
                        foreach (var p in placed)
                            result.Items.Add(p with { Floor = item.Floor, Interior = insideOnly, ParentPlacementId = b.Key });
                        if (placed.Count > 0) Interlocked.Increment(ref _layoutObjects);
                    }
                }
                // Towns without a baked distant mesh: the building's own distant mesh stands in for it far away (what the game's
                // "generate distant towns" would batch).
                if (!isDestroyed && (town is null || !_bakedTowns.Contains(town.InstanceId)) &&
                    WorldObjectLayout.DistantMesh(record, b.Key, position, b.Rotation) is { } distant)
                {
                    result.Stand.Add(distant);
                    Interlocked.Increment(ref _stand);
                }
            }
        }
        if (!withFeatures) return result;
        int featureIndex = zone.Y * WorldLayout.ZoneCount + zone.X;
        var list = featureIndex < Features.Zones.Count ? Features.Zones[featureIndex] : [];
        for (int i = 0; i < list.Count; i++)
            if (WorldObjectLayout.Feature(db, list[i], $"feature {zone} {i}") is { } placed)
            {
                result.Items.Add(placed);
                Interlocked.Increment(ref _features);
            }
        return result;
    }

    /// <summary>A line for the log: what has been laid out so far.</summary>
    public string Describe() =>
        $"{_buildings} buildings ({_fromState} at their state's world Y, {_destroyed} destroyed, {_missing} without a record, {_foliage} foliage resources and {_empty} others without meshes), " +
        $"{_layouts} exterior layouts ({_layoutObjects} objects, {_layoutsNotFound} layout names not found), {_features} map features, {_stand} distant stand-ins";

    public void Dispose()
    {
        if (_heights is null) return;
        foreach (var h in _heights.Values) h.Dispose();
        _heights.Dispose();
        _heights = null;
    }
}