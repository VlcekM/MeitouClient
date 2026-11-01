using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>A building's collision footprint on the ground plane (docs/game/buildings-production.md "Placement"): the convex hull of its collision shapes' X/Z, in the building's own space.</summary>
/// <param name="Hull">Counter-clockwise, scale applied, no rotation or position.</param>
public sealed record BuildingFootprint(Vector2[] Hull, float YMin, float YMax)
{
    /// <summary>The smallest box (X, Z) round <see cref="Hull"/>.</summary>
    public (Vector2 Min, Vector2 Max) Bounds
    {
        get
        {
            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);
            foreach (var p in Hull) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            return (min, max);
        }
    }

    /// <summary>The hull in the world for a building at <paramref name="position"/> turned by <paramref name="rotation"/> (only its effect on X and Z counts).</summary>
    public Vector2[] Place(Vector3 position, Quaternion rotation)
    {
        var q = rotation.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(rotation);
        var result = new Vector2[Hull.Length];
        for (int i = 0; i < Hull.Length; i++)
        {
            var p = Vector3.Transform(new Vector3(Hull[i].X, 0, Hull[i].Y), q);
            result[i] = new Vector2(p.X + position.X, p.Z + position.Z);
        }
        return result;
    }

    /// <summary>The X/Z distance from <paramref name="point"/> to the hull of a building at <paramref name="position"/> turned by <paramref name="rotation"/>, 0 inside it (the hull is convex).</summary>
    public float Gap(Vector3 position, Quaternion rotation, Vector2 point)
    {
        var q = rotation.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(rotation);
        var local3 = Vector3.Transform(new Vector3(point.X - position.X, 0, point.Y - position.Z), Quaternion.Conjugate(q));
        var p = new Vector2(local3.X, local3.Z);
        float nearest = float.MaxValue;
        int left = 0, right = 0;
        for (int i = 0; i < Hull.Length; i++)
        {
            var a = Hull[i];
            var e = Hull[(i + 1) % Hull.Length] - a;
            float cross = e.X * (p.Y - a.Y) - e.Y * (p.X - a.X);
            if (cross > 0) left++; else if (cross < 0) right++;
            float t = e.LengthSquared() > 1e-12f ? Math.Clamp(Vector2.Dot(p - a, e) / e.LengthSquared(), 0, 1) : 0;
            nearest = MathF.Min(nearest, Vector2.Distance(p, a + t * e));
        }
        return left == 0 || right == 0 ? 0 : nearest;
    }
}

/// <summary>A placed building's footprint in the world.</summary>
public readonly record struct PlacedFootprint(string Id, Vector2[] Hull);

/// <summary>A level-data building with the footprint of its record (in its own space), for picking; <paramref name="Plot"/> when it is the <see cref="BuildingFootprints.Plot"/> stand-in.</summary>
public readonly record struct PlacedBuilding(BuildingPlacement Placement, BuildingFootprint Footprint, bool Plot = false);

/// <summary>
/// Footprints of the BUILDING records (from their collision files, the way the navmesh gatherer places them) and of the level data's placed buildings, for the host's placement
/// check. A record is read on a worker the first time it is asked for; until then <see cref="Of"/> says null and the buildings that need it are left out.
/// </summary>
public sealed class BuildingFootprints
{
    readonly GameInstall install;
    readonly GameDatabase db;
    readonly CollisionCache collision;
    readonly Lazy<Dictionary<ZoneCoordinate, List<BuildingPlacement>>> index;
    readonly WorldLevelData? levels;
    readonly ConcurrentDictionary<string, Task<BuildingFootprint?>> byRecord = new(StringComparer.Ordinal);

    public BuildingFootprints(GameInstall install, GameDatabase db, WorldLevelData? levels)
    {
        this.install = install;
        this.db = db;
        this.levels = levels;
        collision = new CollisionCache(install);
        index = new(() =>
        {
            var map = new Dictionary<ZoneCoordinate, List<BuildingPlacement>>();
            if (levels is null) return map;
            foreach (var b in levels.Buildings())
            {
                if (!map.TryGetValue(b.Zone, out var list)) map[b.Zone] = list = [];
                list.Add(b);
            }
            return map;
        });
    }

    /// <summary>The footprint of a BUILDING; null while it is being read, and for a record with no collision.</summary>
    public BuildingFootprint? Of(string buildingId) => Request(buildingId) is { IsCompletedSuccessfully: true } done ? done.Result : null;

    /// <summary>Whether the footprint has been worked out (it may be none).</summary>
    public bool Ready(string buildingId) => Request(buildingId).IsCompleted;

    /// <summary>The footprint of a BUILDING, read now if it was not yet (null: no collision).</summary>
    public BuildingFootprint? Wait(string buildingId) => Request(buildingId).GetAwaiter().GetResult();

    /// <summary>
    /// The placed footprints of the buildings that stand in a walker's way: the long-range grid's obstacles (<see cref="CoarseRoute"/>). Left out (engine choice): IGNORE
    /// <c>path mode</c>, gateways, destroyed ones, and WALKABLE ones walked across rather than round: features (Shield Bridge, Swamp Walkboard) and those whose top is
    /// below the agent height (pontoons, swamp platforms). WALKABLE walls and houses stay: you can stand on them, not walk through. Reads every record's footprint (on
    /// the pool) before it returns.
    /// </summary>
    public IReadOnlyList<Vector2[]> Obstacles(IEnumerable<BuildingPlacement> placements, WorldLevelData levels, Func<double, double, float> terrainHeight)
    {
        var placed = new List<ResolvedBuilding>();
        foreach (var b in placements)
        {
            if (b.TargetsZoneRecord || BuildingPlacements.Resolve(b, db, levels, terrainHeight) is not { Destroyed: false } r) continue;
            var mode = (PathMode)r.Record.GetInt("path mode", (int)PathMode.Obstacle);
            if (mode == PathMode.Ignore || r.Record.GetBool("is gateway") || mode == PathMode.Walkable && r.Record.GetBool("is feature")) continue;
            placed.Add(r);
            _ = Request(r.Record.StringId);
        }
        float agentHeight = new NavBuildSettings().AgentHeight;
        var result = new List<Vector2[]>(placed.Count);
        foreach (var r in placed)
            if (Wait(r.Record.StringId) is { } footprint && !((PathMode)r.Record.GetInt("path mode", (int)PathMode.Obstacle) == PathMode.Walkable && footprint.YMax < agentHeight))
                result.Add(footprint.Place(r.Position, r.Placement.Rotation));
        return result;
    }

    /// <summary>Spacing (units) of the samples a map feature's footprint is taken at (engine choice: a quarter of a coarse route cell).</summary>
    public const float FeatureSample = 48;

    /// <summary>
    /// The map features' footprints (<c>features.dat</c> placements with a MAP_FEATURES <c>collision</c>, docs/game/pathfinding.md "Map features"): the long-range
    /// grid's obstacles beside the buildings'. A feature is no convex block (Mourn's tube is an arch over the town, a cliff has bays), so it is sampled every
    /// <see cref="FeatureSample"/> units: a sample is solid where the nearest of the feature's faces above the terrain faces up (the terrain is inside it, the
    /// navmesh's rule) and lies more than the agent height over the terrain (lower tops are walked over, as low WALKABLE buildings). Each row's run of solid
    /// samples becomes one rectangle.
    /// </summary>
    public IReadOnlyList<Vector2[]> FeatureObstacles(Func<double, double, float> terrainHeight)
    {
        var path = Path.Combine(install.DataDirectory, MapFeatureFile.RelativePath);
        if (!System.IO.File.Exists(path)) return [];
        var features = MapFeatureFile.Open(install).All().Select(t => t.Feature).ToList();
        float agentHeight = new NavBuildSettings().AgentHeight;
        var result = new ConcurrentBag<Vector2[]>();
        var terrainGate = new object();
        Parallel.ForEach(features, f =>
        {
            if (db.Find(f.StringId) is not { Type: FcsRecordType.MAP_FEATURES } record || record.GetPath("collision") is not { Length: > 0 } file
                || collision.Get(file) is not { } prepared) return;
            var node = WorldObjectLayout.InstanceTransform(f.Position, f.Rotation, f.Scale);
            var tris = new List<Vector3>();
            foreach (var shape in prepared.Shapes)
                foreach (int i in shape.Indices) tris.Add(Vector3.Transform(shape.Vertices[i], node));
            if (tris.Count == 0) return;
            float x0 = tris.Min(v => v.X), x1 = tris.Max(v => v.X), z0 = tris.Min(v => v.Z), z1 = tris.Max(v => v.Z);
            int columns = (int)MathF.Floor((x1 - x0) / FeatureSample) + 1, rows = (int)MathF.Floor((z1 - z0) / FeatureSample) + 1;
            // The faces over each sample: height and whether it faces up.
            var hits = new List<(float Y, bool Up)>?[columns * rows];
            for (int t = 0; t < tris.Count; t += 3)
            {
                Vector3 a = tris[t], b = tris[t + 1], c = tris[t + 2];
                float area = (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
                if (MathF.Abs(area) < 1e-6f) continue;
                int i0 = Math.Max(0, (int)MathF.Ceiling((MathF.Min(a.X, MathF.Min(b.X, c.X)) - x0) / FeatureSample)), i1 = Math.Min(columns - 1, (int)MathF.Floor((MathF.Max(a.X, MathF.Max(b.X, c.X)) - x0) / FeatureSample));
                int j0 = Math.Max(0, (int)MathF.Ceiling((MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) - z0) / FeatureSample)), j1 = Math.Min(rows - 1, (int)MathF.Floor((MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) - z0) / FeatureSample));
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        float px = x0 + i * FeatureSample, pz = z0 + j * FeatureSample;
                        if (!NavGeometry.Barycentric(px, pz, a, b, c, out float u, out float v, out float w) || u < 0 || v < 0 || w < 0) continue;
                        (hits[j * columns + i] ??= []).Add((u * a.Y + v * b.Y + w * c.Y, area < 0));
                    }
            }
            for (int j = 0; j < rows; j++)
            {
                int run = -1;
                for (int i = 0; i <= columns; i++)
                {
                    bool solid = false;
                    if (i < columns && hits[j * columns + i] is { } list && MathF.Abs(x0 + i * FeatureSample) < WorldLayout.HalfWorldSize && MathF.Abs(z0 + j * FeatureSample) < WorldLayout.HalfWorldSize)
                    {
                        float px = x0 + i * FeatureSample, pz = z0 + j * FeatureSample, ground, nearest = float.MaxValue, top = float.MinValue;
                        lock (terrainGate) ground = terrainHeight(px, pz);   // the caller's height function need not be thread-safe (a heightmap file)
                        bool up = false;
                        foreach (var (y, faceUp) in list)
                        {
                            if (y > ground && y < nearest) { nearest = y; up = faceUp; }
                            if (faceUp) top = MathF.Max(top, y);
                        }
                        solid = up && top - ground > agentHeight;
                    }
                    if (solid && run < 0) run = i;
                    if (!solid && run >= 0)
                    {
                        float half = FeatureSample / 2, ax = x0 + run * FeatureSample - half, bx = x0 + (i - 1) * FeatureSample + half, z = z0 + j * FeatureSample;
                        result.Add([new(ax, z - half), new(bx, z - half), new(bx, z + half), new(ax, z + half)]);
                        run = -1;
                    }
                }
            }
        });
        return [.. result];
    }

    /// <summary>
    /// The triangles of a building record's collision shapes in its own space (scale applied, as the hull is): what a click on a building is tested against after its hull. Read from the
    /// cache on demand, not kept; empty without a database record or collision files.
    /// </summary>
    public IReadOnlyList<(Vector3[] Vertices, int[] Indices)> Mesh(string buildingId)
    {
        var result = new List<(Vector3[], int[])>();
        if (db.Find(buildingId) is not { } building) return result;
        float scale = building.GetFloat("scale", 1);
        if (scale <= 0) scale = 1;
        foreach (var mesh in WorldObjectLayout.Building(db, building, "footprint", Vector3.Zero, Quaternion.Identity, new BuildingState()))
        {
            string path = mesh.Source.GetPath("xml collision");
            if (path.Length == 0 || collision.Get(path) is not { } prepared) continue;
            foreach (var shape in prepared.Shapes) result.Add(([.. shape.Vertices.Select(v => v * scale)], shape.Indices));
        }
        return result;
    }

    /// <summary>The triangles of a collision file in its own space (an item's <c>physics file</c>), unscaled; empty when it is not in the install or cannot be read.</summary>
    public IReadOnlyList<(Vector3[] Vertices, int[] Indices)> File(string recordPath) =>
        collision.Get(recordPath) is { } prepared ? [.. prepared.Shapes.Select(s => (s.Vertices, s.Indices))] : [];

    readonly ConcurrentDictionary<string, BuildingFootprint?> plots = new(StringComparer.Ordinal);

    /// <summary>
    /// The pick stand-in of a BUILDING without collision (outdoor farms, tents, carpets: docs/formats/collision.md): the hull of its parts' render-mesh bounds boxes, scale and part
    /// placement applied. Only for picking: placement and the navmesh never see it. Null when the record has collision, or no part's bounds can be read.
    /// </summary>
    public BuildingFootprint? Plot(string buildingId) => plots.GetOrAdd(buildingId, id =>
    {
        if (Wait(id) is not null || db.Find(id) is not { } building) return null;
        var points = new List<Vector3>();
        foreach (var mesh in WorldObjectLayout.Building(db, building, "footprint", Vector3.Zero, Quaternion.Identity, new BuildingState()))
        {
            if (Meitou.Data.Physics.CollisionPaths.Resolve(install, mesh.MeshPath) is not { } path || !Meitou.Data.Ogre.OgreMeshReader.TryReadBounds(path, out var b)) continue;
            for (int corner = 0; corner < 8; corner++)
                points.Add(Vector3.Transform(new Vector3((corner & 1) == 0 ? b.Min.X : b.Max.X, (corner & 2) == 0 ? b.Min.Y : b.Max.Y, (corner & 4) == 0 ? b.Min.Z : b.Max.Z), mesh.Transform));
        }
        if (points.Count < 3) return null;
        var hull = NavGeometry.Footprint(points, 0, 0, 0).Polygon;
        return hull.Length < 3 ? null : new BuildingFootprint(hull, points.Min(p => p.Y), points.Max(p => p.Y));
    });

    readonly ConcurrentDictionary<string, (Vector3 Min, Vector3 Max)?> partBounds = new(StringComparer.Ordinal);

    /// <summary>The bounds box of a building part's mesh in its own space (the mesh file's bounds chunk, read once); null when the file is not found or has none.</summary>
    public (Vector3 Min, Vector3 Max)? PartBounds(string meshPath) => partBounds.GetOrAdd(meshPath, path =>
        Meitou.Data.Physics.CollisionPaths.Resolve(install, path) is { } file && Meitou.Data.Ogre.OgreMeshReader.TryReadBounds(file, out var b) && b.Max.X >= b.Min.X ? (b.Min, b.Max) : null);

    /// <summary>Whether a BUILDING is a farm (it names a <c>farm data</c>): the level buildings without collision that a click still selects, by their <see cref="Plot"/>.</summary>
    bool IsFarm(string buildingId) => db.Find(buildingId)?.GetReferences("farm data") is [_, ..];

    Task<BuildingFootprint?> Request(string buildingId) => byRecord.GetOrAdd(buildingId, static (id, self) => Task.Run(() => self.Compute(id)), this);   // a static factory: nothing allocated on a hit

    BuildingFootprint? Compute(string buildingId)
    {
        if (db.Find(buildingId) is not { } building) return null;
        float scale = building.GetFloat("scale", 1);
        if (scale <= 0) scale = 1;
        var points = new List<Vector3>();
        foreach (var mesh in WorldObjectLayout.Building(db, building, "footprint", Vector3.Zero, Quaternion.Identity, new BuildingState()))
        {
            string path = mesh.Source.GetPath("xml collision");
            if (path.Length == 0 || collision.Get(path) is not { } prepared) continue;
            foreach (var shape in prepared.Shapes)
                foreach (var v in shape.Vertices) points.Add(v * scale);
        }
        if (points.Count < 3) return null;
        var hull = NavGeometry.Footprint(points, 0, 0, 0).Polygon;
        return hull.Length < 3 ? null : new BuildingFootprint(hull, points.Min(p => p.Y), points.Max(p => p.Y));
    }

    /// <summary>
    /// The level data's buildings whose placement lies within reach of the ray's ground track: the candidates a click picks from. Their footprints are read now (in parallel) if they
    /// were not yet, so the first click on a building picks it. A farm, which has no collision, comes with its <see cref="Plot"/>.
    /// </summary>
    public IEnumerable<PlacedBuilding> Along(Vector3 origin, Vector3 direction, float maxDistance)
    {
        var near = Near(origin, direction, maxDistance).ToList();
        foreach (var b in near) _ = Request(b.BuildingId);
        foreach (var b in near)
            if (Wait(b.BuildingId) is { } footprint) yield return new PlacedBuilding(b, footprint);
            else if (IsFarm(b.BuildingId) && Plot(b.BuildingId) is { } plot) yield return new PlacedBuilding(b, plot, Plot: true);
    }

    IEnumerable<BuildingPlacement> Near(Vector3 origin, Vector3 direction, float maxDistance)
    {
        var map = index.Value;
        const float Reach = 200;
        var from = new Vector2(origin.X, origin.Z);
        var track = new Vector2(direction.X, direction.Z) * maxDistance;
        float trackLength = track.Length();
        var zones = new HashSet<ZoneCoordinate>();
        int samples = (int)MathF.Ceiling(trackLength / (WorldLayout.ZoneSize / 2f)) + 1;
        for (int i = 0; i <= samples; i++)
        {
            var at = from + track * (i / (float)samples);
            var home = WorldLayout.ZoneOf(at.X, at.Y);
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++) zones.Add(new ZoneCoordinate(home.X + dx, home.Y + dz));
        }
        float lengthSquared = track.LengthSquared();
        foreach (var zone in zones)
        {
            if (!map.TryGetValue(zone, out var list)) continue;
            foreach (var b in list)
            {
                if (b.Position == Vector3.Zero || b.TargetsZoneRecord) continue;
                var p = new Vector2(b.Position.X, b.Position.Z);
                float s = lengthSquared < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(p - from, track) / lengthSquared, 0, 1);
                if (Vector2.DistanceSquared(p, from + track * s) > Reach * Reach) continue;
                yield return b;
            }
        }
    }

    /// <summary>
    /// The level data's buildings with <c>links together</c> (town walls, whatever their owner or state) placed within <paramref name="radius"/> of <paramref name="centre"/> (X, Z), with
    /// their absolute position and turn: the wall pieces a player's wall can snap to. <paramref name="terrainHeight"/> sets a placement that has no height of its own.
    /// </summary>
    public IEnumerable<(string Key, string Record, Vector3 Position, Quaternion Rotation)> LinkedAround(Vector2 centre, float radius, Func<double, double, float> terrainHeight)
    {
        if (levels is null) yield break;
        var map = index.Value;
        var home = WorldLayout.ZoneOf(centre.X, centre.Y);
        int span = (int)MathF.Ceiling(radius / WorldLayout.ZoneSize);
        for (int dz = -span; dz <= span; dz++)
            for (int dx = -span; dx <= span; dx++)
            {
                if (!map.TryGetValue(new ZoneCoordinate(home.X + dx, home.Y + dz), out var list)) continue;
                foreach (var b in list)
                {
                    if (b.TargetsZoneRecord || b.Position == Vector3.Zero || Vector2.DistanceSquared(new Vector2(b.Position.X, b.Position.Z), centre) > radius * radius) continue;
                    if (db.Find(b.BuildingId)?.GetBool("links together") == true && BuildingPlacements.Resolve(b, db, levels, terrainHeight) is { } r) yield return (b.Key, b.BuildingId, r.Position, b.Rotation);
                }
            }
    }

    /// <summary>The footprints (those already read; the others are being read) of the level data's buildings with a placement within <paramref name="radius"/> of <paramref name="centre"/> (X, Z).</summary>
    public IEnumerable<PlacedFootprint> StaticAround(Vector2 centre, float radius)
    {
        var map = index.Value;
        const float Reach = 200;   // a building's own extent beyond its placement point
        var home = WorldLayout.ZoneOf(centre.X, centre.Y);
        int span = (int)MathF.Ceiling((radius + Reach) / WorldLayout.ZoneSize);
        for (int dz = -span; dz <= span; dz++)
            for (int dx = -span; dx <= span; dx++)
            {
                if (!map.TryGetValue(new ZoneCoordinate(home.X + dx, home.Y + dz), out var list)) continue;
                foreach (var b in list)
                {
                    if (b.Position == Vector3.Zero || Vector2.DistanceSquared(new Vector2(b.Position.X, b.Position.Z), centre) > (radius + Reach) * (radius + Reach)) continue;
                    if (Of(b.BuildingId) is { } footprint) yield return new PlacedFootprint(b.Key, footprint.Place(b.Position, b.Rotation));
                }
            }
    }
}
