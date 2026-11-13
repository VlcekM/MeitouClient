using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// Streams the world's objects by zone: the zones within a range of the eye are laid out on worker threads
/// (<see cref="WorldObjects.BuildZone"/>) and kept until the eye is a zone's width further away than that range.
/// An instance starts unresolved, with the placement position and a guessed size; <see cref="Scan"/> asks for its mesh once it is
/// near enough to matter and resolves it (real bounds, draw distance, materials) when the mesh is resident.
/// </summary>
sealed class ObjectStreamer(WorldObjects objects, ObjectMeshCache meshes) : IDisposable
{
    #region Instances

    /// <summary>
    /// How much nearer than its placement point an unresolved instance counts (a guess at its size): a mesh is wanted a little before it can be seen.
    /// </summary>
    public const float RequestMargin = 600;

    /// <summary>
    /// How far outside its zone a parked part's centre may be (a part's bounds; generous).
    /// </summary>
    const float ParkedSlack = 1000;

    /// <summary>
    /// One placed mesh.
    /// </summary>
    public sealed class Instance
    {
        /// <summary>
        /// The placement: its mesh, record, kind, owner and transform as the layout made it.
        /// </summary>
        public required PlacedMesh Placed;

        /// <summary>
        /// The placement's mesh in the mesh cache (a distant stand-in's is a distant mesh).
        /// </summary>
        public required ObjectMesh Mesh;

        /// <summary>
        /// Where the instance is drawn: the placement's transform, or a posed part's or ghost's own.
        /// </summary>
        public Matrix4x4 Transform;

        /// <summary>
        /// An animated part (door, turret joint, rotor): the transform it was drawn with in the last frame that wrote motion
        /// (<c>WorldObjectRenderer.NoteMotion</c>) and that frame's number.
        /// </summary>
        public Matrix4x4 LastDrawn;

        /// <summary>
        /// The number of the frame <see cref="LastDrawn"/> was drawn in (-2: never).
        /// </summary>
        public long LastDrawnFrame = -2;

        /// <summary>
        /// The traced scene's lamp caches: this instance's mesh and transform hash and the frame it last changed (<see cref="Gi.GiScene"/>).
        /// </summary>
        public ulong CasterHash;

        /// <summary>
        /// The frame <see cref="CasterHash"/> last changed in.
        /// </summary>
        public long CasterChanged;

        /// <summary>
        /// World bounding sphere (the placement point and a guess until resolved).
        /// </summary>
        public Vector3 Centre;

        /// <summary>
        /// The bounding radius (world units, scaled): 400 until resolved.
        /// </summary>
        public float Radius = 400;

        /// <summary>
        /// <see cref="MeshLod.Value"/>-like distance beyond which the instance is not drawn (<see cref="ObjectRanges.PartRenderingDistance"/>).
        /// Kept on an unload (<c>WorldObjectRenderer.Unresolve</c>), so <see cref="Scan"/> knows the part distance before the mesh is back.
        /// </summary>
        public float Limit = float.MaxValue;

        /// <summary>
        /// A map feature drawn with the terrain material.
        /// </summary>
        public bool TerrainMode;

        /// <summary>
        /// A building's distant mesh standing in for a town without a baked one.
        /// </summary>
        public bool Stand;

        /// <summary>
        /// A landmark (<see cref="LandmarkClass"/>): kept in <see cref="LandmarkZone"/>, drawn to the landmark distance. Its <see cref="Radius"/> is
        /// the true world radius from the start, so <see cref="ScanLandmarks"/> asks for its mesh as soon as its bounds could be in range.
        /// </summary>
        public bool Landmark;

        /// <summary>
        /// A door part: the open amount its <see cref="Transform"/> was last posed at (-1: not yet; <c>DoorPoses.Pose</c>).
        /// </summary>
        public float DoorOpen = -1;

        /// <summary>
        /// A turret part (<see cref="PlacedMesh.Joints"/>): the <see cref="TurretPose.Version"/> its <see cref="Transform"/> was last posed at (0:
        /// from its placement; <c>JointPoses.Pose</c>).
        /// </summary>
        public int TurretStamp;

        /// <summary>
        /// A part under a rotor: the <c>RotatingParts.Advance</c> frame its <see cref="Transform"/> was last posed at (-1: not yet).
        /// </summary>
        public long RotorFrame = -1;

        /// <summary>
        /// The mesh the instance resolved to (null: waiting for it); a draw that finds the mesh no longer holds it unresolves the instance.
        /// </summary>
        public GpuObjectMesh? Gpu;

        /// <summary>
        /// One material per part, made when the instance resolved (null until then).
        /// </summary>
        public ObjectMaterialSet? Materials;

        /// <summary>
        /// Whether it stands in a loaded building's interior, as last found (<c>WorldObjectRenderer.InInterior</c>); one object, so a view recorded
        /// on another thread reads it whole.
        /// </summary>
        public InteriorAnswer? Interior;

        /// <summary>
        /// A colour its albedo is multiplied by (a farm plant's, <c>FarmPlants.Set</c>), packed by
        /// <c>FarmPlants.PackTint</c>; 0: none.
        /// </summary>
        public float Tint;
    }

    /// <summary>
    /// An interior test's answer: for the interiors of <see cref="InteriorVersion"/> <paramref name="Version"/>, at <paramref name="At"/>.
    /// <paramref name="Building"/> is the building whose hull holds it (with a small margin) and <paramref name="Floor"/> the floor of that hull it
    /// stands on (<c>WorldObjectRenderer.StandsAbove</c>).
    /// </summary>
    public sealed record InteriorAnswer(int Version, Vector3 At, bool Inside, string? Building, int Floor);

    #endregion

    #region Zones

    /// <summary>
    /// The laid-out zones by coordinate.
    /// </summary>
    readonly Dictionary<ZoneCoordinate, Zone> _zones = [];

    /// <summary>
    /// The zone layouts running on workers, by coordinate.
    /// </summary>
    readonly Dictionary<ZoneCoordinate, Task<ZoneObjects>> _jobs = [];

    /// <summary>
    /// The zones with placements inside the grid and their origins (world units), the ones <see cref="Update"/> lays out within range; a runtime
    /// building adds its zone.
    /// </summary>
    readonly List<(ZoneCoordinate Coordinate, double X0, double Z0)> _populated =
        objects.PopulatedZones
            .Where(z => z.IsInsideGrid)
            .Select(z =>
            {
                var (x, zz) = WorldLayout.ZoneOrigin(z);
                return (z, x, zz);
            })
            .ToList();

    /// <summary>
    /// The scratch list <see cref="ZonesNear"/> returns (cleared at each call).
    /// </summary>
    readonly List<Zone> _near = [];

    /// <summary>
    /// Changes whenever the loaded interiors do: a zone with interiors comes or goes, or a zone's interiors change (an answer of an older version is
    /// asked again).
    /// </summary>
    public int InteriorVersion { get; private set; }

    /// <summary>
    /// Unloads <paramref name="coordinate"/>'s zone; a new <see cref="InteriorVersion"/> only when it had interiors.
    /// </summary>
    void Drop(ZoneCoordinate coordinate)
    {
        if (_zones.Remove(coordinate, out var gone) && gone.Interiors.Length > 0) InteriorVersion++;
    }

    /// <summary>
    /// A laid-out zone: its real instances, its distant stand-ins, those still waiting for their meshes, and its buildings' indoor volumes.
    /// </summary>
    public sealed class Zone
    {
        /// <summary>
        /// The zone's coordinate (-1, -1 for the landmark pseudo-zone).
        /// </summary>
        public required ZoneCoordinate Coordinate;

        /// <summary>
        /// The zone's origin in the world (x, z, world units).
        /// </summary>
        public double X0, Z0;

        /// <summary>
        /// The full-detail instances.
        /// </summary>
        public List<Instance> Real { get; } = [];

        /// <summary>
        /// The distant stand-ins (buildings' distant meshes) drawn past the object distance.
        /// </summary>
        public List<Instance> Stand { get; } = [];

        /// <summary>
        /// The instances whose meshes are not resolved yet, scanned while in range (<see cref="Scan"/>).
        /// </summary>
        public List<Instance> Unresolved { get; } = [];

        /// <summary>
        /// Unresolved instances out of range, out of <see cref="Unresolved"/> until the eye comes near (<see cref="Scan"/>): the largest part
        /// distance of the real ones (<see cref="Instance.Limit"/>, so the object distance when unknown) and whether any are distant stand-ins (back
        /// by the distant range).
        /// </summary>
        public List<Instance> Parked { get; } = [];

        /// <summary>
        /// The largest part distance among the real instances of <see cref="Parked"/>.
        /// </summary>
        public float ParkedLimit;

        /// <summary>
        /// Some instance of <see cref="Parked"/> is a distant stand-in.
        /// </summary>
        public bool ParkedStand;

        /// <summary>
        /// The indoor volumes of the zone's buildings (<see cref="ZoneObjects.Interiors"/>). An array, so the per-frame interior walks (camera,
        /// audio, lamps, GI) enumerate without allocating.
        /// </summary>
        public InteriorVolume[] Interiors = [];

        /// <summary>
        /// The gateway hulls of the zone (<see cref="ZoneObjects.GateHulls"/>): only the audio's indoor ray tests them.
        /// </summary>
        public InteriorVolume[] GateHulls = [];
    }

    /// <summary>
    /// Zones laid out at once.
    /// </summary>
    public int MaxJobs { get; set; } = 3;

    /// <summary>
    /// No new zone layouts are started (memory pressure, <see cref="VramGuard"/>) but within <see cref="PausedReach"/>; the ones in flight are taken
    /// in.
    /// </summary>
    public bool Paused { get; set; }

    /// <summary>
    /// While <see cref="Paused"/>, zones this near the eye are still laid out (<see cref="VramGuard.NearReach"/>; 0: none).
    /// </summary>
    public float PausedReach { get; set; }

    #endregion

    #region Landmarks and staging

    /// <summary>
    /// The placements behind <see cref="Staged"/>.
    /// </summary>
    IReadOnlyList<PlacedMesh>? _staged;

    /// <summary>
    /// The world's landmarks as one pseudo-zone (null: landmarks are off, they stay in their zones). Placements that are landmarks are left out of
    /// the zones' instances (<see cref="Landmarks"/> decides, the same test that made the list) and kept here for good: a few hundred, walked every
    /// frame.
    /// </summary>
    public Zone? LandmarkZone { get; private set; }

    /// <summary>
    /// Set before the zones are laid out: from then on <see cref="Update"/> leaves landmark placements out of them.
    /// </summary>
    public LandmarkClass? Landmarks { get; set; }

    /// <summary>
    /// No zone is laid out until the landmarks are collected (<see cref="SetLandmarks"/>), or a zone laid out before would hold them too.
    /// </summary>
    public bool WaitForLandmarks { get; set; }

    /// <summary>
    /// Presentation-only placements added to the zones they stand in (the Meitou title's staging). Setting or clearing it lays those zones out again;
    /// null, the world as the game places it, in play.
    /// </summary>
    public IReadOnlyList<PlacedMesh>? Staged
    {
        get => _staged;
        set
        {
            var touched = (_staged ?? []).Concat(value ?? []).Select(ZoneOf).Distinct().ToList();
            _staged = value;
            foreach (var coordinate in touched)
            {
                Drop(coordinate);
                _jobs.Remove(coordinate);
                if (!_populated.Any(p => p.Coordinate == coordinate))
                {
                    var (x, z) = WorldLayout.ZoneOrigin(coordinate);
                    _populated.Add((coordinate, x, z));
                }
            }
        }
    }

    /// <summary>
    /// The zone a placement stands in, by its translation.
    /// </summary>
    static ZoneCoordinate ZoneOf(PlacedMesh p) => WorldLayout.ZoneOf(p.Transform.M41, p.Transform.M43);

    /// <summary>
    /// Makes the landmark pseudo-zone from the collected placements (the render thread, once) and lets the zones be laid out without them.
    /// </summary>
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

    #endregion

    #region Runtime buildings

    /// <summary>
    /// The runtime buildings' layouts running on workers: their zone, the placement key, whether they replace the zone's level building, the job.
    /// </summary>
    readonly List<(ZoneCoordinate Zone, string Id, bool Replace, Task<ZoneObjects> Job)> _runtimeJobs = [];

    // ---- runtime buildings (WorldObjects.AddRuntime / RemoveRuntime; docs/simulation.md "Buildings", host seam) ----

    /// <summary>
    /// A runtime placement was added to <see cref="WorldObjects"/>: a zone laid out later includes it; a zone already laid out gets only its
    /// instances (laid out on a worker, taken in by <see cref="Update"/>), so the rest of the zone does not flicker. A zone still being laid out is
    /// laid out again.
    /// </summary>
    public void AddRuntime(BuildingPlacement placement)
    {
        var zone = placement.Zone;
        EnsurePopulated(zone);
        if (!_zones.ContainsKey(zone) && !_jobs.ContainsKey(zone)) return;
        bool replace = placement.DestroyedOverride is not null;
        if (_jobs.ContainsKey(zone) || !replace && _fills.Any(f => f.Zone.Coordinate == zone))
        {
            Invalidate(zone);
            return;
        }
        _runtimeJobs.RemoveAll(j => j.Id == placement.Key);
        _runtimeJobs.Add((zone, placement.Key, replace, BackgroundWork.Run(() => objects.BuildRuntime(placement))));
    }

    /// <summary>
    /// A runtime placement was removed from <see cref="WorldObjects"/>: its instances and indoor volume leave the zone.
    /// </summary>
    public void RemoveRuntime(string key, string instanceId, ZoneCoordinate zone)
    {
        _runtimeJobs.RemoveAll(j => j.Id == key);
        if (_jobs.ContainsKey(zone) || _fills.Any(f => f.Zone.Coordinate == zone))
        {
            Invalidate(zone);
            return;
        }

        if (!_zones.TryGetValue(zone, out var z)) return;
        RemoveInstances(z, key, instanceId);
    }

    /// <summary>
    /// Takes a building's instances out of a zone: those named by the placement's instance id or its key and their children (the parts are named by
    /// the placement's instance id and its indoor volume by its key; a player's building has the one name for both).
    /// </summary>
    void RemoveInstances(Zone z, string key, string? instanceId = null)
    {
        // a part with no parent is no match for a missing instance id
        bool Named(string? id) => id is not null && (id == key || id == instanceId);
        bool Match(Instance i) => Named(i.Placed.PlacementId) || Named(i.Placed.ParentPlacementId);
        z.Real.RemoveAll(Match);
        z.Stand.RemoveAll(Match);
        z.Unresolved.RemoveAll(Match);
        z.Parked.RemoveAll(Match);
        int had = z.Interiors.Length;
        z.Interiors = [.. z.Interiors.Where(v => !Named(v.PlacementId))];
        if (z.Interiors.Length != had) InteriorVersion++;
        z.GateHulls = [.. z.GateHulls.Where(v => v.PlacementId != instanceId)];
    }

    /// <summary>
    /// Adds a zone inside the grid to the zones laid out within range (a runtime building's zone may have had no placements).
    /// </summary>
    void EnsurePopulated(ZoneCoordinate zone)
    {
        if (!zone.IsInsideGrid || _populated.Any(p => p.Coordinate == zone)) return;
        var (x, z) = WorldLayout.ZoneOrigin(zone);
        _populated.Add((zone, x, z));
    }

    /// <summary>
    /// Lays a zone out again from scratch: dropped, its layout and runtime jobs forgotten, kept among the zones to lay out.
    /// </summary>
    void Invalidate(ZoneCoordinate zone)
    {
        Drop(zone);
        _jobs.Remove(zone);
        _runtimeJobs.RemoveAll(j => j.Zone == zone);
        EnsurePopulated(zone);
    }

    /// <summary>
    /// Reloads every zone in <paramref name="coordinates"/> (the placements of a replaced world are gone).
    /// </summary>
    public void Reload(IEnumerable<ZoneCoordinate> coordinates)
    {
        foreach (var zone in coordinates) Invalidate(zone);
    }

    /// <summary>
    /// Takes the runtime buildings' finished layouts into their resident zones (not while a zone is still being filled): a replacing house's old
    /// instances out, the new ones in, their interiors and gate hulls added to the zone's.
    /// </summary>
    void TakeRuntime()
    {
        for (int i = _runtimeJobs.Count - 1; i >= 0; i--)
        {
            var (zone, id, replace, job) = _runtimeJobs[i];
            if (!job.IsCompleted || _fills.Any(f => f.Zone.Coordinate == zone)) continue;
            _runtimeJobs.RemoveAt(i);
            if (!job.IsCompletedSuccessfully || !objects.HasRuntime(id) || !_zones.TryGetValue(zone, out var z)) continue;
            var laid = job.Result;

            // Keep the resident zone until the replacement layout is ready; only this house and its furniture change.
            if (replace) RemoveInstances(z, id);
            // the zone's own layout already had it (a level-editor building's parts are named by its instance id, not its key)
            else if (z.Real.Any(inst => inst.Placed.PlacementId == id
                || laid.Items.Count > 0 && inst.Placed.PlacementId == laid.Items[0].PlacementId)) continue;
            foreach (var item in laid.Items) z.Real.Add(Make(item, stand: false, z));
            foreach (var item in laid.Stand) z.Stand.Add(Make(item, stand: true, z));
            if (laid.Interiors.Count > 0)
            {
                z.Interiors = [.. z.Interiors, .. laid.Interiors];
                InteriorVersion++;
            }

            if (laid.GateHulls.Count > 0) z.GateHulls = [.. z.GateHulls, .. laid.GateHulls];
        }
    }

    /// <summary>
    /// The zones laid out.
    /// </summary>
    public int Loaded => _zones.Count;

    /// <summary>
    /// Every laid-out zone (a struct enumerator: walked every frame).
    /// </summary>
    public Dictionary<ZoneCoordinate, Zone>.ValueCollection AllZones => _zones.Values;

    /// <summary>
    /// The laid-out zone at a coordinate, or null.
    /// </summary>
    public Zone? ZoneAt(ZoneCoordinate coordinate) => _zones.GetValueOrDefault(coordinate);

    /// <summary>
    /// Zones in range not laid out yet, and those being laid out.
    /// </summary>
    public int Pending => _jobs.Count + Wanted + _fills.Count;

    /// <summary>
    /// The zones in range not laid out yet, as the last <see cref="Update"/> counted them.
    /// </summary>
    public int Wanted { get; private set; }

    /// <summary>
    /// Every instance of the zones and the landmarks.
    /// </summary>
    public int Instances => _zones.Values.Sum(z => z.Real.Count + z.Stand.Count) + (LandmarkZone?.Real.Count ?? 0);

    /// <summary>
    /// The instances resolved to a mesh, of the zones and the landmarks.
    /// </summary>
    public int Resolved =>
        _zones.Values.Sum(z => z.Real.Count(i => i.Gpu is not null) + z.Stand.Count(i => i.Gpu is not null))
        + (LandmarkZone?.Real.Count(i => i.Gpu is not null) ?? 0);

    #endregion

    #region Kept points

    /// <summary>
    /// Where the camera is about to be (a character flight's arrival eye, docs/render-objects.md "Arrival prefetch"), or null: zones are laid out and
    /// kept, and meshes asked for, by the nearer of the eye and this point, so the arrival's objects load during the flight.
    /// </summary>
    public Vector3? Ahead { get; set; }

    /// <summary>
    /// Extra points (the player's characters, "Keep textures at characters") laid out, loaded and kept like the eye's surroundings, but ranked after
    /// them.
    /// </summary>
    public IReadOnlyList<Vector3> Kept { get; set; } = [];

    /// <summary>
    /// How near a kept point may be to the eye or to an earlier kept point and still be dropped (<see cref="MergeKept"/>).
    /// </summary>
    public const float KeptMergeDistance = 500;

    /// <summary>
    /// <paramref name="points"/> less those within <see cref="KeptMergeDistance"/> of <paramref name="eye"/> or of a point already taken: a squad
    /// standing together is one point. Every distance test (<see cref="Near"/>, per unresolved instance per frame) walks the kept points, so ten
    /// members cost ten times the scan (docs/render-objects.md "Kept points"); the merge moves a distance by at most this much.
    /// </summary>
    public static IReadOnlyList<Vector3> MergeKept(IReadOnlyList<Vector3> points, Vector3 eye)
    {
        if (points.Count == 0) return points;
        var taken = new List<Vector3>(Math.Min(points.Count, 4));
        foreach (var p in points)
        {
            if (Vector3.Distance(p, eye) <= KeptMergeDistance) continue;
            bool near = false;
            foreach (var t in taken)
            {
                if (Vector3.Distance(p, t) <= KeptMergeDistance)
                {
                    near = true;
                    break;
                }
            }

            if (!near) taken.Add(p);

        }
        return taken;
    }

    /// <summary>
    /// The distance from <paramref name="p"/> to the nearest kept point (infinity: none). Indexed: a foreach over the interface boxed an enumerator
    /// per call, for every object and zone scanned.
    /// </summary>
    float NearKept(Vector3 p)
    {
        float d = float.PositiveInfinity;
        var kept = Kept;
        for (int i = 0; i < kept.Count; i++)
            d = MathF.Min(d, Vector3.Distance(kept[i], p));

        return d;
    }

    /// <summary>
    /// The distance from a zone's square to the nearest kept point (infinity: none).
    /// </summary>
    float NearKeptZone(double x0, double z0)
    {
        float d = float.PositiveInfinity;
        var kept = Kept;
        for (int i = 0; i < kept.Count; i++)
            d = MathF.Min(d, ZoneDistance(x0, z0, kept[i]));

        return d;
    }

    #endregion

    #region Distances

    /// <summary>
    /// Distance from the nearest of <paramref name="eye"/>, <see cref="Ahead"/> and <see cref="Kept"/> to <paramref name="p"/>.
    /// </summary>
    public float Near(Vector3 eye, Vector3 p)
    {
        float d = MathF.Min(Vector3.Distance(eye, p), NearKept(p));
        return Ahead is { } a ? MathF.Min(d, Vector3.Distance(a, p)) : d;
    }

    /// <summary>
    /// The distance that starts new work for <paramref name="p"/> (a mesh request, a finer texture or mesh level): <see cref="Near"/>, but during a
    /// flight only from <see cref="Ahead"/> and <see cref="Kept"/>, so the flight path keeps what it has and loads nothing (docs/render-objects.md
    /// "Arrival prefetch").
    /// </summary>
    public float LoadNear(Vector3 eye, Vector3 p) => Ahead is { } a ? MathF.Min(Vector3.Distance(a, p), NearKept(p)) : Near(eye, p);

    /// <summary>
    /// The camera's distance to its target: a kept point's detail (texture mips, mesh levels) is measured from at least this far, the view a
    /// jump to that character would have, instead of from the ground at its feet. What is loaded and kept still counts from the point itself.
    /// </summary>
    public float KeptViewDistance { get; set; }

    /// <summary>
    /// <see cref="LoadNear"/> for detail: a kept point counts from at least <see cref="KeptViewDistance"/>.
    /// </summary>
    public float DetailNear(Vector3 eye, Vector3 p)
    {
        float kept = MathF.Max(NearKept(p), KeptViewDistance);
        if (Ahead is { } a) return MathF.Min(Vector3.Distance(a, p), kept);
        return MathF.Min(Vector3.Distance(eye, p), kept);
    }

    /// <summary>
    /// A zone's distance as the zones are loaded by: from the lifted eye, the nearest kept point or, during a flight, <see cref="Ahead"/>.
    /// </summary>
    float NearZone(double x0, double z0, Vector3 eye)
    {
        float d = MathF.Min(EyeLift.Apply(ZoneDistance(x0, z0, eye), EyeLift.Of(eye, GroundUnderEye)), NearKeptZone(x0, z0));
        return Ahead is { } a ? MathF.Min(d, ZoneDistance(x0, z0, a)) : d;
    }

    /// <summary>
    /// The load order (smaller first): the distance from the eye, or during a flight from <see cref="Ahead"/>, what is near the eye (the flight path)
    /// coming after everything within <paramref name="lead"/> of the arrival, so the landing view loads first.
    /// </summary>
    public float Rank(Vector3 eye, Vector3 p, float lead)
    {
        float d = Vector3.Distance(eye, p);
        if (Kept.Count > 0) d = MathF.Min(d, NearKept(p) + lead);
        return Ahead is { } a ? MathF.Min(Vector3.Distance(a, p), d + lead) : d;
    }

    /// <summary>
    /// A zone's load order (smaller first), as <see cref="Rank"/> for a point: from the eye, or behind the kept points and the arrival
    /// by <paramref name="lead"/>.
    /// </summary>
    float RankZone(double x0, double z0, Vector3 eye, float lead)
    {
        float d = ZoneDistance(x0, z0, eye);
        if (Kept.Count > 0) d = MathF.Min(d, NearKeptZone(x0, z0) + lead);
        return Ahead is { } a ? MathF.Min(ZoneDistance(x0, z0, a), d + lead) : d;
    }

    /// <summary>
    /// The ground's height under the eye (set each frame; unknown: none): which zones are laid out, kept and unparked counts the eye's height
    /// over it beyond the game's cameras (<see cref="EyeLift"/>), the kept points and a flight's arrival not.
    /// </summary>
    public float GroundUnderEye { get; set; } = float.NegativeInfinity;

    /// <summary>
    /// Distance on the ground plane from <paramref name="eye"/> to a zone's square.
    /// </summary>
    public static float ZoneDistance(double x0, double z0, Vector3 eye)
    {
        double dx = Math.Max(Math.Max(x0 - eye.X, eye.X - (x0 + WorldLayout.ZoneSize)), 0);
        double dz = Math.Max(Math.Max(z0 - eye.Z, eye.Z - (z0 + WorldLayout.ZoneSize)), 0);
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }

    #endregion

    #region Streaming

    /// <summary>
    /// The laid-out zones whose instances are being made, oldest first.
    /// </summary>
    readonly Queue<Fill> _fills = [];

    /// <summary>
    /// Starts laying out the zones within <paramref name="range"/> (nearest first), takes finished ones in, drops far ones.
    /// <paramref name="covered"/>: under the loading screen, layouts run on every streaming thread and taken zones are filled for up to 16 ms a frame
    /// (frames need not be smooth there).
    /// </summary>
    public void Update(Vector3 eye, float range, int maxNew, bool covered = false)
    {
        TakeRuntime();
        int taken = 0;
        foreach (var (coordinate, task) in _jobs.ToArray())
        {
            if (!task.IsCompleted || taken >= maxNew) continue;
            _jobs.Remove(coordinate);
            taken++;
            try
            {
                Add(task.Result);
            }
            catch (AggregateException e)
            {
                Console.WriteLine($"warning   zone {coordinate}: {e.InnerException?.Message ?? e.Message}");
            }
        }
        var wanted = new List<(ZoneCoordinate Coordinate, float Rank, float Distance)>();
        foreach (var (coordinate, x0, z0) in _populated)
        {
            if (_zones.ContainsKey(coordinate) || _jobs.ContainsKey(coordinate)) continue;
            float distance = NearZone(x0, z0, eye);
            if (distance <= range) wanted.Add((coordinate, RankZone(x0, z0, eye, range), distance));
        }
        foreach (var (coordinate, _, distance) in wanted.OrderBy(w => w.Rank))
        {
            if (_jobs.Count >= (covered ? Math.Max(MaxJobs, BackgroundWork.Threads) : MaxJobs) || WaitForLandmarks) break;
            if (Paused && distance > PausedReach) continue;
            var landmarkClass = Landmarks;
            var extra = _staged;
            _jobs[coordinate] = BackgroundWork.Run(() =>
            {
                var laid = objects.BuildZone(coordinate);
                // they live in LandmarkZone
                if (landmarkClass is not null) laid.Items.RemoveAll(landmarkClass.IsLandmark);
                if (extra is not null) laid.Items.AddRange(extra.Where(p => ZoneOf(p) == coordinate));
                return laid;
            });
        }
        Wanted = wanted.Count(w => !_jobs.ContainsKey(w.Coordinate));
        foreach (var zone in _zones.Values.ToArray())
            if (NearZone(zone.X0, zone.Z0, eye) > range + WorldLayout.ZoneSize) Drop(zone.Coordinate);
        FillZones(maxNew > 8 ? double.MaxValue : covered ? 16 : 1.0);
    }

    /// <summary>
    /// A zone laid out by a worker whose instances are being made, a few at a time (<see cref="FillZones"/>).
    /// </summary>
    sealed class Fill
    {
        /// <summary>
        /// The zone the instances go into.
        /// </summary>
        public required Zone Zone;

        /// <summary>
        /// The layout from the worker.
        /// </summary>
        public required ZoneObjects Laid;

        /// <summary>
        /// The next of the layout's placements to make (real ones first, then the stand-ins).
        /// </summary>
        public int Index;
    }

    /// <summary>
    /// Takes a worker's layout as the zone (replacing any older one) and queues its instances to be made.
    /// </summary>
    void Add(ZoneObjects laid)
    {
        var (x0, z0) = WorldLayout.ZoneOrigin(laid.Zone);
        var zone = new Zone { Coordinate = laid.Zone, X0 = x0, Z0 = z0, Interiors = [.. laid.Interiors], GateHulls = [.. laid.GateHulls] };
        if (laid.Interiors.Count > 0 || _zones.TryGetValue(laid.Zone, out var old) && old.Interiors.Length > 0) InteriorVersion++;
        _zones[laid.Zone] = zone;
        _fills.Enqueue(new Fill { Zone = zone, Laid = laid });
    }

    /// <summary>
    /// Makes the instances of laid-out zones until <paramref name="budgetMs"/> has passed (a big zone has thousands; making them all in one frame
    /// cost up to 30 ms).
    /// </summary>
    void FillZones(double budgetMs)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (_fills.TryPeek(out var f))
        {
            // dropped meanwhile
            if (!_zones.TryGetValue(f.Zone.Coordinate, out var current) || !ReferenceEquals(current, f.Zone))
            {
                _fills.Dequeue();
                continue;
            }

            int real = f.Laid.Items.Count, total = real + f.Laid.Stand.Count;
            while (f.Index < total)
            {
                int i = f.Index++;
                if (i < real) f.Zone.Real.Add(Make(f.Laid.Items[i], stand: false, f.Zone));
                else f.Zone.Stand.Add(Make(f.Laid.Stand[i - real], stand: true, f.Zone));
                if ((f.Index & 63) == 0 && watch.Elapsed.TotalMilliseconds >= budgetMs) return;
            }
            _fills.Dequeue();
        }
    }

    /// <summary>
    /// Makes an instance of a placement, unresolved: TERRAIN-mode for a map feature or staged foliage mesh whose texturing is 2 (the mesh then keeps
    /// its level indices).
    /// </summary>
    Instance Make(PlacedMesh p, bool stand, Zone zone)
    {
        // A map feature names its texturing "texture mode", a foliage mesh (staged by the Meitou title) "material type"; 2 is TERRAIN in both.
        bool terrainMode = p.Kind == PlacedKind.MapFeature
            && p.Source.GetInt(p.Source.Type == Data.Fcs.FcsRecordType.FoliageMesh ? "material type" : "texture mode") == 2;
        var mesh = meshes.Get(p.MeshPath, distant: stand);
        if (terrainMode) mesh.KeepLevelIndices = true;
        var inst = new Instance { Placed = p, Mesh = mesh, Transform = p.Transform, Centre = p.Transform.Translation, TerrainMode = terrainMode,
            Stand = stand };
        zone.Unresolved.Add(inst);
        return inst;
    }

    #endregion

    #region Scanning

    /// <summary>
    /// Brings back the parked instances of <paramref name="zone"/> that the eye could now see: the zone's square, less the margin and a part's size.
    /// </summary>
    void UnparkNear(Zone zone, Vector3 eye, float objectRange, float distantRange, bool noDistant)
    {
        // Parked instances come back once the eye could be within their range (the zone's square, less the margin and a part's size). The ranges
        // are the current ones, so a longer distance (settings, the VRAM guard's recovery) brings them back too.
        float parkedRange = Math.Max(Math.Min(objectRange, zone.ParkedLimit), zone.ParkedStand && !noDistant ? distantRange : 0);
        bool due = zone.Parked.Count > 0 && NearZone(zone.X0, zone.Z0, eye) - RequestMargin - ParkedSlack <= parkedRange;
        if (!due) return;

        for (int i = zone.Parked.Count - 1; i >= 0; i--)
        {
            var inst = zone.Parked[i];
            if (Near(eye, inst.Centre) - RequestMargin > (inst.Stand ? distantRange : Math.Min(objectRange, inst.Limit))) continue;

            zone.Unresolved.Add(inst);
            zone.Parked[i] = zone.Parked[^1];
            zone.Parked.RemoveAt(zone.Parked.Count - 1);
        }
    }

    /// <summary>
    /// Moves <c>list[i]</c> (one of <paramref name="zone"/>'s unresolved instances) out of the list walked every frame, into the parked ones
    /// (thousands at long object distances; each test walks the kept points too).
    /// </summary>
    static void ParkAt(Zone zone, List<Instance> list, int i)
    {
        var inst = list[i];
        zone.Parked.Add(inst);
        if (inst.Stand) zone.ParkedStand = true;
        else zone.ParkedLimit = Math.Max(zone.ParkedLimit, inst.Limit);
        list[i] = list[^1];
        list.RemoveAt(list.Count - 1);
    }

    /// <summary>
    /// Asks for the meshes of unresolved instances near enough to matter (nearest first, through the mesh cache) and resolves those whose mesh
    /// is resident. Returns how many are still waiting for a mesh.
    /// </summary>
    public int Scan(Vector3 eye, float objectRange, float distantRange, bool noDistant, Func<Instance, bool> resolve, int maxResolve = int.MaxValue)
    {
        int waiting = 0, resolved = 0;

        // During a flight the arrival's zones take the resolve budget first.
        IEnumerable<Zone> zones = Ahead is null
            ? (IEnumerable<Zone>)_zones.Values
            : _zones.Values.OrderBy(z => RankZone(z.X0, z.Z0, eye, objectRange));
        foreach (var zone in zones)
        {
            UnparkNear(zone, eye, objectRange, distantRange, noDistant);
            var list = zone.Unresolved;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var inst = list[i];
                if (inst.Stand && noDistant) continue;

                // A part beyond its part distance (known once it was resolved) is not drawn, so its mesh is not wanted: asking for it would load
                // a mesh nothing marks as used, unloaded a minute later and asked for again (docs/renderer-native.md 8.17).
                float range = inst.Stand ? distantRange : Math.Min(objectRange, inst.Limit);

                // The placement point, less a guess at the size: a mesh is wanted a little before it can be seen.
                float d = Near(eye, inst.Centre) - RequestMargin;
                if (d > range)
                {
                    ParkAt(zone, list, i);
                    continue;
                }
                switch (inst.Mesh.Status)
                {
                    case ObjectMesh.State.None:
                        // Kept unparked (the zone test would bring it back every frame), but only near the arrival or a kept point is it asked for
                        // during a flight.
                        if (Ahead is null || LoadNear(eye, inst.Centre) - RequestMargin <= range)
                            meshes.Request(inst.Mesh, d, Rank(eye, inst.Centre, range) - RequestMargin);

                        waiting++;
                        break;
                    case ObjectMesh.State.Loading or ObjectMesh.State.Uploading:
                        waiting++;
                        break;
                    case ObjectMesh.State.Resident when resolved >= maxResolve:
                        waiting++;
                        break;
                    case ObjectMesh.State.Resident:
                        // the mesh does not hold the level this instance needs yet (it is being remade)
                        if (!resolve(inst))
                        {
                            waiting++;
                            break;
                        }

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
    /// <see cref="Scan"/> for the landmarks: <paramref name="reach"/> is how far they are drawn (the instance's own range is the smaller of that and
    /// its part distance, as in <see cref="Scan"/>). A landmark's radius is known, so its mesh is asked for once its bounds could be in range,
    /// however big it is. There are a few hundred: the whole list is walked every frame. Returns how many are waiting for a mesh.
    /// </summary>
    public int ScanLandmarks(Vector3 eye, float reach, Func<Instance, bool> resolve, int maxResolve = int.MaxValue) =>
        LandmarkZone is { } zone ? ScanLoose(zone, eye, reach, resolve, maxResolve) : 0;

    #endregion

    #region Loose

    /// <summary>
    /// The pseudo-zone of instances that belong to no zone (the placement ghost): kept for good, never streamed out, scanned every frame by
    /// <see cref="ScanLoose"/> like the landmarks.
    /// </summary>
    public Zone Loose { get; } = new() { Coordinate = new ZoneCoordinate(-2, -2) };

    /// <summary>
    /// A new instance in <see cref="Loose"/> (resolved by <see cref="ScanLoose"/> once its mesh is resident).
    /// </summary>
    public Instance MakeLoose(PlacedMesh placed)
    {
        var inst = Make(placed, stand: false, Loose);
        Loose.Real.Add(inst);
        return inst;
    }

    /// <summary>
    /// Forgets every loose instance.
    /// </summary>
    public void ClearLoose()
    {
        Loose.Real.Clear();
        Loose.Unresolved.Clear();
    }

    /// <summary>
    /// Forgets one loose instance.
    /// </summary>
    public void DropLoose(Instance inst)
    {
        Loose.Real.Remove(inst);
        Loose.Unresolved.Remove(inst);
    }

    /// <summary>
    /// The scan of <see cref="ScanLandmarks"/> over one pseudo-zone's unresolved instances.
    /// </summary>
    public int ScanLoose(Zone zone, Vector3 eye, float reach, Func<Instance, bool> resolve, int maxResolve = int.MaxValue)
    {
        int waiting = 0, resolved = 0;
        var list = zone.Unresolved;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var inst = list[i];
            float range = Math.Min(reach, inst.Limit);
            float d = Near(eye, inst.Centre) - Math.Max(RequestMargin, inst.Radius);
            if (d > range) continue;
            switch (inst.Mesh.Status)
            {
                case ObjectMesh.State.None:
                    meshes.Request(inst.Mesh, d, Rank(eye, inst.Centre, range) - Math.Max(RequestMargin, inst.Radius));
                    waiting++;
                    break;
                case ObjectMesh.State.Loading or ObjectMesh.State.Uploading:
                    waiting++;
                    break;
                case ObjectMesh.State.Resident when resolved >= maxResolve:
                    waiting++;
                    break;
                case ObjectMesh.State.Resident:
                    if (!resolve(inst))
                    {
                        waiting++;
                        break;
                    }

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

    #endregion

    #region Zones in view

    /// <summary>
    /// The loaded zones within <paramref name="range"/> of the eye whose square may be seen through <paramref name="frustum"/>.
    /// </summary>
    public List<Zone> ZonesNear(Vector3 eye, float range, Vector4[] frustum)
    {
        _near.Clear();
        foreach (var zone in _zones.Values)
        {
            if (ZoneDistance(zone.X0, zone.Z0, eye) > range) continue;
            var min = new Vector3((float)zone.X0, -2000, (float)zone.Z0);
            var max = new Vector3((float)zone.X0 + WorldLayout.ZoneSize, 20000, (float)zone.Z0 + WorldLayout.ZoneSize);
            if (WorldCamera.Intersects(frustum, min, max)) _near.Add(zone);
        }

        // The dictionary enumerates in the order zones were taken in (worker completion), and the batches' and the instances' order, the depth ties
        // and the draw log follow it: by position, a frame draws in the same order whatever the streaming did.
        _near.Sort(static (a, b) => a.X0 != b.X0 ? a.X0.CompareTo(b.X0) : a.Z0.CompareTo(b.Z0));
        return _near;
    }

    #endregion

    /// <summary>
    /// Waits for the zone layouts still running (up to 5 s each).
    /// </summary>
    public void Dispose()
    {
        foreach (var job in _jobs.Values)
        {
            try
            {
                job.Wait(5000);
            }
            catch (AggregateException)
            {
            }
        }
    }
}