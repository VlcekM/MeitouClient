using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Rendering;

/// <summary>
/// Streams the world's objects by zone: the zones within a range of the eye are laid out on worker threads
/// (<see cref="WorldObjects.BuildZone"/>) and kept until the eye is a zone's width further away than that range.
/// An instance starts unresolved, with the placement position and a guessed size; <see cref="Scan"/> asks for its mesh once it is
/// near enough to matter and resolves it (real bounds, draw distance, materials) when the mesh is resident.
/// </summary>
sealed class ObjectStreamer(WorldObjects objects, ObjectMeshCache meshes) : IDisposable
{
    /// <summary>How much nearer than its placement point an unresolved instance counts (a guess at its size): a mesh is wanted a little before it can be seen.</summary>
    public const float RequestMargin = 600;
    /// <summary>How far outside its zone a parked part's centre may be (a part's bounds; generous).</summary>
    const float ParkedSlack = 1000;

    /// <summary>One placed mesh.</summary>
    public sealed class Instance
    {
        public required PlacedMesh Placed;
        public required ObjectMesh Mesh;
        public Matrix4x4 Transform;
        /// <summary>World bounding sphere (the placement point and a guess until resolved).</summary>
        public Vector3 Centre;
        public float Radius = 400;
        /// <summary><see cref="MeshLod.Value"/>-like distance beyond which the instance is not drawn (<see cref="ObjectRanges.PartRenderingDistance"/>).
        /// Kept on an unload (<c>WorldObjectRenderer.Unresolve</c>), so <see cref="Scan"/> knows the part distance before the mesh is back.</summary>
        public float Limit = float.MaxValue;
        /// <summary>A map feature drawn with the terrain material.</summary>
        public bool TerrainMode;
        /// <summary>A building's distant mesh standing in for a town without a baked one.</summary>
        public bool Stand;
        /// <summary>A landmark (<see cref="LandmarkClass"/>): kept in <see cref="LandmarkZone"/>, drawn to the landmark distance. Its <see cref="Radius"/> is
        /// the true world radius from the start, so <see cref="ScanLandmarks"/> asks for its mesh as soon as its bounds could be in range.</summary>
        public bool Landmark;
        public GpuObjectMesh? Gpu;
        public ObjectMaterialSet? Materials;
    }

    public sealed class Zone
    {
        public required ZoneCoordinate Coordinate;
        public double X0, Z0;
        public List<Instance> Real { get; } = [];
        public List<Instance> Stand { get; } = [];
        public List<Instance> Unresolved { get; } = [];
        /// <summary>Unresolved parts beyond their part distance, out of <see cref="Unresolved"/> until the eye comes near (<see cref="Scan"/>), and the largest of their distances.</summary>
        public List<Instance> Parked { get; } = [];
        public float ParkedLimit;
    }

    /// <summary>Zones laid out at once.</summary>
    public int MaxJobs { get; set; } = 3;
    /// <summary>No new zone layouts are started (memory pressure, <see cref="VramGuard"/>); the ones in flight are taken in.</summary>
    public bool Paused { get; set; }

    /// <summary>
    /// The world's landmarks as one pseudo-zone (null: landmarks are off, they stay in their zones). Placements that are landmarks are left out of the
    /// zones' instances (<see cref="Landmarks"/> decides, the same test that made the list) and kept here for good: a few hundred, walked every frame.
    /// </summary>
    public Zone? LandmarkZone { get; private set; }
    /// <summary>Set before the zones are laid out: from then on <see cref="Update"/> leaves landmark placements out of them.</summary>
    public LandmarkClass? Landmarks { get; set; }
    /// <summary>No zone is laid out until the landmarks are collected (<see cref="SetLandmarks"/>), or a zone laid out before would hold them too.</summary>
    public bool WaitForLandmarks { get; set; }

    /// <summary>Makes the landmark pseudo-zone from the collected placements (the render thread, once) and lets the zones be laid out without them.</summary>
    public void SetLandmarks(List<(PlacedMesh Placed, float Radius)> placements)
    {
        WaitForLandmarks = false;
        var zone = new Zone { Coordinate = new ZoneCoordinate(-1, -1) };
        foreach (var (placed, radius) in placements)
        {
            var inst = Make(placed, stand: false, zone);
            inst.Landmark = true;
            inst.Radius = radius;
            zone.Real.Add(inst);
        }
        LandmarkZone = zone;
    }

    readonly Dictionary<ZoneCoordinate, Zone> zones = [];
    readonly Dictionary<ZoneCoordinate, Task<ZoneObjects>> jobs = [];
    readonly List<(ZoneCoordinate Coordinate, double X0, double Z0)> populated =
        objects.PopulatedZones.Where(z => z.IsInsideGrid).Select(z => { var (x, zz) = WorldLayout.ZoneOrigin(z); return (z, x, zz); }).ToList();
    readonly List<Zone> near = [];

    public int Loaded => zones.Count;
    public IEnumerable<Zone> AllZones => zones.Values;
    /// <summary>Zones in range not laid out yet, and those being laid out.</summary>
    public int Pending => jobs.Count + Wanted + fills.Count;
    public int Wanted { get; private set; }
    public int Instances => zones.Values.Sum(z => z.Real.Count + z.Stand.Count) + (LandmarkZone?.Real.Count ?? 0);
    public int Resolved => zones.Values.Sum(z => z.Real.Count(i => i.Gpu is not null) + z.Stand.Count(i => i.Gpu is not null)) + (LandmarkZone?.Real.Count(i => i.Gpu is not null) ?? 0);

    /// <summary>Distance on the ground plane from <paramref name="eye"/> to a zone's square.</summary>
    public static float ZoneDistance(double x0, double z0, Vector3 eye)
    {
        double dx = Math.Max(Math.Max(x0 - eye.X, eye.X - (x0 + WorldLayout.ZoneSize)), 0);
        double dz = Math.Max(Math.Max(z0 - eye.Z, eye.Z - (z0 + WorldLayout.ZoneSize)), 0);
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Starts laying out the zones within <paramref name="range"/> (nearest first), takes finished ones in, drops far ones.</summary>
    public void Update(Vector3 eye, float range, int maxNew)
    {
        int taken = 0;
        foreach (var (coordinate, task) in jobs.ToArray())
        {
            if (!task.IsCompleted || taken >= maxNew) continue;
            jobs.Remove(coordinate);
            taken++;
            try { Add(task.Result); }
            catch (AggregateException e) { Console.WriteLine($"warning   zone {coordinate}: {e.InnerException?.Message ?? e.Message}"); }
        }
        var wanted = new List<(ZoneCoordinate Coordinate, float Distance)>();
        foreach (var (coordinate, x0, z0) in populated)
        {
            if (zones.ContainsKey(coordinate) || jobs.ContainsKey(coordinate)) continue;
            float d = ZoneDistance(x0, z0, eye);
            if (d <= range) wanted.Add((coordinate, d));
        }
        foreach (var (coordinate, _) in wanted.OrderBy(w => w.Distance))
        {
            if (jobs.Count >= MaxJobs || Paused || WaitForLandmarks) break;
            var landmarkClass = Landmarks;
            jobs[coordinate] = BackgroundWork.Run(() =>
            {
                var laid = objects.BuildZone(coordinate);
                if (landmarkClass is not null) laid.Items.RemoveAll(landmarkClass.IsLandmark);   // they live in LandmarkZone
                return laid;
            });
        }
        Wanted = wanted.Count(w => !jobs.ContainsKey(w.Coordinate));
        foreach (var zone in zones.Values.ToArray())
            if (ZoneDistance(zone.X0, zone.Z0, eye) > range + WorldLayout.ZoneSize) zones.Remove(zone.Coordinate);
        FillZones(maxNew > 8 ? double.MaxValue : 1.0);
    }

    /// <summary>A zone laid out by a worker whose instances are being made, a few at a time (<see cref="FillZones"/>).</summary>
    sealed class Fill
    {
        public required Zone Zone;
        public required ZoneObjects Laid;
        public int Index;
    }

    readonly Queue<Fill> fills = [];

    void Add(ZoneObjects laid)
    {
        var (x0, z0) = WorldLayout.ZoneOrigin(laid.Zone);
        var zone = new Zone { Coordinate = laid.Zone, X0 = x0, Z0 = z0 };
        zones[laid.Zone] = zone;
        fills.Enqueue(new Fill { Zone = zone, Laid = laid });
    }

    /// <summary>Makes the instances of laid-out zones until <paramref name="budgetMs"/> has passed (a big zone has thousands; making them all in one frame cost up to 30 ms).</summary>
    void FillZones(double budgetMs)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (fills.TryPeek(out var f))
        {
            if (!zones.TryGetValue(f.Zone.Coordinate, out var current) || !ReferenceEquals(current, f.Zone)) { fills.Dequeue(); continue; }   // dropped meanwhile
            int real = f.Laid.Items.Count, total = real + f.Laid.Stand.Count;
            while (f.Index < total)
            {
                int i = f.Index++;
                if (i < real) f.Zone.Real.Add(Make(f.Laid.Items[i], stand: false, f.Zone));
                else f.Zone.Stand.Add(Make(f.Laid.Stand[i - real], stand: true, f.Zone));
                if ((f.Index & 63) == 0 && watch.Elapsed.TotalMilliseconds >= budgetMs) return;
            }
            fills.Dequeue();
        }
    }

    Instance Make(PlacedMesh p, bool stand, Zone zone)
    {
        bool terrainMode = p.Kind == PlacedKind.MapFeature && p.Source.GetInt("texture mode") == 2;
        var mesh = meshes.Get(p.MeshPath, distant: stand);
        if (terrainMode) mesh.KeepLevelIndices = true;
        var inst = new Instance { Placed = p, Mesh = mesh, Transform = p.Transform, Centre = p.Transform.Translation, TerrainMode = terrainMode, Stand = stand };
        zone.Unresolved.Add(inst);
        return inst;
    }

    /// <summary>
    /// Asks for the meshes of unresolved instances near enough to matter (nearest first, through the mesh cache) and resolves those whose mesh
    /// is resident. Returns how many are still waiting for a mesh.
    /// </summary>
    public int Scan(Vector3 eye, float objectRange, float distantRange, bool noDistant, Func<Instance, bool> resolve, int maxResolve = int.MaxValue)
    {
        int waiting = 0, resolved = 0;
        foreach (var zone in zones.Values)
        {
            var list = zone.Unresolved;
            // Parked parts come back once the eye could be within their part distance (the zone's square, less the margin and a part's size).
            if (zone.Parked.Count > 0 && ZoneDistance(zone.X0, zone.Z0, eye) - RequestMargin - ParkedSlack <= zone.ParkedLimit)
            {
                for (int i = zone.Parked.Count - 1; i >= 0; i--)
                {
                    var inst = zone.Parked[i];
                    if (Vector3.Distance(eye, inst.Centre) - RequestMargin > Math.Min(objectRange, inst.Limit)) continue;
                    list.Add(inst);
                    zone.Parked[i] = zone.Parked[^1];
                    zone.Parked.RemoveAt(zone.Parked.Count - 1);
                }
            }
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var inst = list[i];
                if (inst.Stand && noDistant) continue;
                // A part beyond its part distance (known once it was resolved) is not drawn, so its mesh is not wanted: asking for it would load
                // a mesh nothing marks as used, unloaded a minute later and asked for again (docs/renderer-native.md 8.17).
                float range = inst.Stand ? distantRange : Math.Min(objectRange, inst.Limit);
                // The placement point, less a guess at the size: a mesh is wanted a little before it can be seen.
                float d = Vector3.Distance(eye, inst.Centre) - RequestMargin;
                if (d > range)
                {
                    // Parked out of the list walked every frame (thousands at long object distances).
                    if (!inst.Stand && inst.Limit < objectRange)
                    {
                        zone.Parked.Add(inst);
                        zone.ParkedLimit = Math.Max(zone.ParkedLimit, inst.Limit);
                        list[i] = list[^1];
                        list.RemoveAt(list.Count - 1);
                    }
                    continue;
                }
                switch (inst.Mesh.Status)
                {
                    case ObjectMesh.State.None:
                        meshes.Request(inst.Mesh, d);
                        waiting++;
                        break;
                    case ObjectMesh.State.Loading or ObjectMesh.State.Uploading:
                        waiting++;
                        break;
                    case ObjectMesh.State.Resident when resolved >= maxResolve:
                        waiting++;
                        break;
                    case ObjectMesh.State.Resident:
                        if (!resolve(inst)) { waiting++; break; }   // the mesh does not hold the level this instance needs yet (it is being remade)
                        resolved++;
                        list[i] = list[^1];
                        list.RemoveAt(list.Count - 1);
                        break;
                    default:
                        list[i] = list[^1];
                        list.RemoveAt(list.Count - 1);
                        break;
                }
            }
        }
        return waiting;
    }

    /// <summary>
    /// <see cref="Scan"/> for the landmarks: <paramref name="reach"/> is how far they are drawn (the instance's own range is the smaller of that and its part
    /// distance, as in <see cref="Scan"/>). A landmark's radius is known, so its mesh is asked for once its bounds could be in range, however big it is.
    /// There are a few hundred: the whole list is walked every frame. Returns how many are waiting for a mesh.
    /// </summary>
    public int ScanLandmarks(Vector3 eye, float reach, Func<Instance, bool> resolve, int maxResolve = int.MaxValue)
    {
        if (LandmarkZone is not { } zone) return 0;
        int waiting = 0, resolved = 0;
        var list = zone.Unresolved;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var inst = list[i];
            float range = Math.Min(reach, inst.Limit);
            float d = Vector3.Distance(eye, inst.Centre) - Math.Max(RequestMargin, inst.Radius);
            if (d > range) continue;
            switch (inst.Mesh.Status)
            {
                case ObjectMesh.State.None:
                    meshes.Request(inst.Mesh, d);
                    waiting++;
                    break;
                case ObjectMesh.State.Loading or ObjectMesh.State.Uploading:
                    waiting++;
                    break;
                case ObjectMesh.State.Resident when resolved >= maxResolve:
                    waiting++;
                    break;
                case ObjectMesh.State.Resident:
                    if (!resolve(inst)) { waiting++; break; }
                    resolved++;
                    list[i] = list[^1];
                    list.RemoveAt(list.Count - 1);
                    break;
                default:
                    list[i] = list[^1];
                    list.RemoveAt(list.Count - 1);
                    break;
            }
        }
        return waiting;
    }

    /// <summary>The loaded zones within <paramref name="range"/> of the eye whose square may be seen through <paramref name="frustum"/>.</summary>
    public List<Zone> ZonesNear(Vector3 eye, float range, Vector4[] frustum)
    {
        near.Clear();
        foreach (var zone in zones.Values)
        {
            if (ZoneDistance(zone.X0, zone.Z0, eye) > range) continue;
            var min = new Vector3((float)zone.X0, -2000, (float)zone.Z0);
            var max = new Vector3((float)zone.X0 + WorldLayout.ZoneSize, 20000, (float)zone.Z0 + WorldLayout.ZoneSize);
            if (WorldCamera.Intersects(frustum, min, max)) near.Add(zone);
        }
        // The dictionary enumerates in the order zones were taken in (worker completion), and the batches' and the instances' order, the depth ties and
        // the draw log follow it: by position, a frame draws in the same order whatever the streaming did.
        near.Sort(static (a, b) => a.X0 != b.X0 ? a.X0.CompareTo(b.X0) : a.Z0.CompareTo(b.Z0));
        return near;
    }

    public void Dispose()
    {
        foreach (var job in jobs.Values) { try { job.Wait(5000); } catch (AggregateException) { } }
    }
}
