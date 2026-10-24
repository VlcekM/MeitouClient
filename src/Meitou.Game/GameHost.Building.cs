using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Construction;

namespace Meitou.Game;

/// <summary>
/// The host's side of player construction (docs/simulation.md "Buildings", "Host seam: snapshot diff"): the snapshot's <see cref="WorldSnapshot.Buildings"/> are diffed by id
/// whenever <see cref="WorldSnapshot.BuildingsRevision"/> changes. A new site goes into the renderer's runtime placements and, when it qualifies, the navmesh overlay; a removed
/// one leaves both; a completed one enters the navmesh and lights its lamps. The scaffold of a site that is not finished is read from <c>Progress / Target</c> every frame.
/// </summary>
sealed partial class GameHost
{
    World? buildingsWorld;
    long buildingsRevision = -1;
    // What the overlays were last told, by instance id.
    readonly Dictionary<string, BuildingSnapshot> shownBuildings = new(StringComparer.Ordinal);
    readonly HashSet<string> navBuildings = new(StringComparer.Ordinal), litBuildings = new(StringComparer.Ordinal), effectBuildings = new(StringComparer.Ordinal);
    readonly Dictionary<string, float> constructionLook = new(StringComparer.Ordinal);
    Dictionary<string, BuildingBuildData>? buildData;

    /// <summary>The build fields of every BUILDING, by id (read once from the game database).</summary>
    Dictionary<string, BuildingBuildData> BuildData() =>
        buildData ??= scene.Database is { } db ? BuildingBuildData.LoadAll(db).GroupBy(b => b.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal) : [];

    /// <summary>Once a frame, before the world is drawn.</summary>
    void UpdateBuildings()
    {
        if (!ReferenceEquals(buildingsWorld, session.World))
        {
            // A new world (a loaded save, a new game): the old world's sites go, the new one's first snapshot carries all of its own.
            ForgetBuildings();
            buildingsWorld = session.World;
        }
        var snapshot = session.CurrentSnapshot;
        if (snapshot.BuildingsRevision != buildingsRevision)
        {
            DiffBuildings(snapshot);
            buildingsRevision = snapshot.BuildingsRevision;
        }
        // A lit building's lamps burn at its power share (granted / demand, 1 for one that draws nothing): dark without supply (docs/formats/lights.md "When a light is on").
        if (gpu.Lamps is { } lamps && litBuildings.Count > 0)
            foreach (var m in snapshot.Machines)
                if (m.Power is { Kind: Meitou.Simulation.Production.PowerKind.Consumer } pw && litBuildings.Contains(m.Site))
                    lamps.SetRuntimePower(m.Site, pw.Demand > 0 ? pw.Granted / pw.Demand : 1);
        UpdateGroundItems(snapshot);
        UpdateDoors();
        UpdateTurretPoses(snapshot);
        UpdateFarms(snapshot);
        if (gpu.Objects is not { } objects) return;
        constructionLook.Clear();
        foreach (var site in snapshot.Buildings)
            if (!site.Complete) constructionLook[site.Id] = ConstructionLook(site);
        objects.Construction = constructionLook.Count == 0 ? null : constructionLook;
    }

    /// <summary>
    /// The look's <c>f</c> (docs/game/buildings-production.md "Construction look"): (min(progress / target, 1) - threshold) / (1 - threshold), threshold = clamp(build threshold / 100, 0, 0.95).
    /// Below 0 the whole building is scaffold, so it is held at 0.
    /// </summary>
    internal float ConstructionLook(BuildingSnapshot site)
    {
        float threshold = BuildData().TryGetValue(site.Record, out var data) ? Math.Clamp(data.BuildThreshold / 100, 0, 0.95f) : 0;
        float done = site.Target > 0 ? Math.Min(site.Progress / site.Target, 1) : 1;
        return Math.Clamp((done - threshold) / (1 - threshold), 0, 1);
    }

    void DiffBuildings(WorldSnapshot snapshot)
    {
        var current = new Dictionary<string, BuildingSnapshot>(StringComparer.Ordinal);
        foreach (var site in snapshot.Buildings) current[site.Id] = site;
        foreach (string id in shownBuildings.Keys.Where(id => !current.ContainsKey(id)).ToArray())
        {
            gpu.Objects?.RemoveRuntime(id);
            if (navBuildings.Remove(id)) nav?.RemoveRuntimeBuilding(id);
            if (litBuildings.Remove(id)) gpu.Lamps?.RemoveRuntime(id);
            if (effectBuildings.Remove(id)) gpu.Particles?.RemoveBuildingEffects(id);
            shownBuildings.Remove(id);
        }
        foreach (var site in current.Values)
        {
            var placement = Placement(site);
            if (shownBuildings.TryGetValue(site.Id, out var shown) && shown.Record == site.Record && shown.Position == site.Position && shown.Rotation == site.Rotation)
            {
                if (site.Complete && !shown.Complete) AddToNavmesh(site, placement);   // newly complete
                UpdateLamps(site);
                UpdateEffects(site);
                shownBuildings[site.Id] = site;
                continue;
            }
            if (shown is not null && navBuildings.Remove(site.Id)) nav?.RemoveRuntimeBuilding(site.Id);   // moved or changed: out, then in again
            if (shown is not null && litBuildings.Remove(site.Id)) gpu.Lamps?.RemoveRuntime(site.Id);
            if (shown is not null && effectBuildings.Remove(site.Id)) gpu.Particles?.RemoveBuildingEffects(site.Id);
            gpu.Objects?.AddRuntime(placement);
            AddToNavmesh(site, placement);
            UpdateLamps(site);
            UpdateEffects(site);
            shownBuildings[site.Id] = site;
        }
    }

    /// <summary>
    /// A building's lamps are lit while it is complete (docs/game/buildings-production.md "Construction tick": completing switches its lights on). Placed the way the world's
    /// are (<see cref="WorldLights.FromPlaced"/>), without interior layouts: a player building has none.
    /// </summary>
    void UpdateLamps(BuildingSnapshot site)
    {
        if (gpu.Lamps is not { } lamps) return;
        if (!site.Complete)
        {
            if (litBuildings.Remove(site.Id)) lamps.RemoveRuntime(site.Id);
            return;
        }
        if (!litBuildings.Add(site.Id) || scene.Database is not { } db || db.Find(site.Record) is not { } record) return;
        var placed = new List<PlacedLight>();
        WorldObjectLayout.Building(db, record, site.Id, site.Position, site.Rotation, new BuildingState { Identity = site.Id }, placed);
        lamps.SetRuntime(site.Id, placed.Select(p => WorldLights.FromPlaced(p, interior: false)));
    }

    /// <summary>A building's effects (a campfire's fire; docs/formats/lights.md "Effects on buildings") run while it is complete, as its lamps.</summary>
    void UpdateEffects(BuildingSnapshot site)
    {
        if (gpu.Particles is not { } particles) return;
        if (!site.Complete)
        {
            if (effectBuildings.Remove(site.Id)) particles.RemoveBuildingEffects(site.Id);
            return;
        }
        if (!effectBuildings.Add(site.Id) || scene.Database is not { } db || db.Find(site.Record) is not { } record) return;
        var placed = new List<PlacedEffect>();
        WorldObjectLayout.Building(db, record, site.Id, site.Position, site.Rotation, new BuildingState { Identity = site.Id }, null, placed);
        particles.SetBuildingEffects(site.Id, placed);
    }

    /// <summary>
    /// The render and navigation overlay type of a site (<c>StateId</c> null, <c>WorldY</c> the absolute height, the zone of its position). Its key is its own id: a player
    /// building's id is unique in the world (the simulation's registry reserves every saved id), and the overlays remove it by that id.
    /// </summary>
    static BuildingPlacement Placement(BuildingSnapshot site) =>
        new(WorldLayout.ZoneOf(site.Position.X, site.Position.Z), site.Id, site.Record, site.Position, site.Rotation, null, site.Position.Y) { Key = site.Id, Contained = site.Parent is not null };

    /// <summary>Navmesh membership (engine reading of the Observed rebuild on placing): <c>path mode</c> not 0 and the building complete or without a <c>build threshold</c>.</summary>
    void AddToNavmesh(BuildingSnapshot site, BuildingPlacement placement)
    {
        if (nav is null || navBuildings.Contains(site.Id) || !BuildData().TryGetValue(site.Record, out var data)) return;
        if (data.PathMode == 0 || !site.Complete && data.BuildThreshold != 0) return;
        navBuildings.Add(site.Id);
        nav.AddRuntimeBuilding(placement);   // the zones loaded are built again; UpdateNav sends the RepathCommand when one is ready
    }

    void ForgetBuildings()
    {
        gpu?.Objects?.ClearRuntime();
        gpu?.Lamps?.ClearRuntime();
        foreach (string id in effectBuildings) gpu?.Particles?.RemoveBuildingEffects(id);   // the world's own building effects stay
        effectBuildings.Clear();
        litBuildings.Clear();
        levelOwners = null;
        ForgetGroundItems();
        ForgetFarms();
        foreach (string id in navBuildings) nav?.RemoveRuntimeBuilding(id);
        navBuildings.Clear();
        shownBuildings.Clear();
        constructionLook.Clear();
        buildingsRevision = -1;
        if (gpu?.Objects is { } objects) objects.Construction = null;
    }

    /// <summary>
    /// What the player's build mode needs (docs/simulation.md "Buildings", PlayerInterface.Build.cs): the catalog of BUILDINGs, their texts and parts, the placement services
    /// (terrain, collision footprints) and the ghost drawing. Null without a game database.
    /// </summary>
    Building.BuildServices? MakeBuildServices()
    {
        if (scene.Database is not { } db) return null;
        var footprints = new Meitou.Navigation.BuildingFootprints(install, db, IsSandbox || IsDuel || IsBattle ? null : scene.Objects?.Levels);
        var parts = new Dictionary<string, IReadOnlyList<Meitou.Data.World.PlacedMesh>>(StringComparer.Ordinal);
        var icons = new Meitou.Data.Gameplay.Construction.BuildingIcons(Path.Combine(install.DataDirectory, "icons", "buildings"));
        IReadOnlyList<Meitou.Data.Gameplay.Construction.PlacementTown>? towns = null;
        Dictionary<(Meitou.Data.World.ZoneCoordinate, string), (string Town, string? Owner)>? townOf = null;
        var pickable = new Dictionary<string, bool>(StringComparer.Ordinal);
        // Resource and node buildings are not selectable objects (TownBuildings leaves them out too).
        bool Pickable(string record) => pickable.TryGetValue(record, out bool known) ? known
            : pickable[record] = db.Find(record) is { } r && !r.GetBool("is node") && !r.GetBool("is foliage");
        return new Building.BuildServices
        {
            Menu = new Building.BuildMenu([.. BuildData().Values]),
            ItemName = id => db.Find(id)?.Name ?? id,
            Description = id => db.Find(id)?.GetString("description") ?? "",
            Parts = id =>
            {
                if (parts.TryGetValue(id, out var cached)) return cached;
                return parts[id] = db.Find(id) is { } record
                    ? Meitou.Data.World.WorldObjectLayout.Building(db, record, "ghost:" + id, System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity, new Meitou.Data.World.BuildingState())
                    : [];
            },
            IconFile = icons.Find,
            Placement = new Building.PlacementServices
            {
                Ground = gpu.Terrain.HeightAt,
                Footprint = footprints.Of,
                CollisionMesh = footprints.Mesh,
                StaticAround = footprints.StaticAround,
                StaticAlong = (origin, direction, length) => footprints.Along(origin, direction, length).Where(b => Pickable(b.Placement.BuildingId)),
                // The movement pick's floors up to the viewed one: the build window's floor arrows choose the floor the ghost stands on.
                FloorAlong = (origin, direction, length) => gpu.Objects?.FloorHit(origin, direction, viewedFloor, length) is { } hit
                    ? (hit.Position, GhostFloorOf(hit.Room, hit.Room.FloorAt(hit.Position))) : null,
            },
            WorldInfo = placement =>
            {
                var (zone, instance, record) = (placement.Zone, placement.InstanceId, placement.BuildingId);
                if (townOf is null && popData is { } d)
                {
                    townOf = [];
                    foreach (var site in d.Sites)
                    {
                        // A building whose state names no owner belongs to its town's faction (the original re-owns a town's buildings to it).
                        string? townOwner = site.Index < d.SiteFactions.Count && d.SiteFactions[site.Index] is >= 0 and var townFaction && townFaction < d.Factions.Count ? d.Factions[townFaction].Name : null;
                        foreach (var b in site.Buildings)
                            townOf[(b.Zone, b.InstanceId)] = (site.Town.Name, b.OwnerFactionId is { } id ? d.Factions.FirstOrDefault(f => f.Id == id)?.Name ?? townOwner : townOwner);
                    }
                }
                string recordName = db.Find(record)?.Name ?? record;
                var levels = scene.Objects?.Levels;
                var state = levels is not null && placement.StateId is { } stateId && levels.Zones.TryGetValue(zone, out var zoneDb) ? zoneDb.Find(stateId) : null;
                // The squad template that runs it names a special building ("The Dancing Skeleton") and, for a trader, lists what it sells.
                var squad = state?.GetString("squad template") is { Length: > 0 } squadId ? db.Find(squadId) : null;
                string name = squad?.GetString("building name") is { Length: > 0 } squadName ? squadName
                    : state?.GetString("name") is { Length: > 0 } own ? own : recordName;
                string? type = name != recordName ? recordName : null;
                // A shop whose state only carries its layout's generic name ("Shop", "Big Shop"): the Meitou UI calls it after what it sells.
                string? sells = squad is { } s && s.GetBool("is trader") && state?.GetString("interior layout name") is { } layout && layout.Contains("Shop", StringComparison.Ordinal) && name == layout
                    && s.GetReferences("vendors").FirstOrDefault().TargetStringId is { } vendor && db.Find(vendor)?.Name is { Length: > 0 } list
                    ? char.ToUpperInvariant(list[0]) + list[1..] : null;
                var (town, owner) = townOf is not null && townOf.TryGetValue((zone, instance), out var info) ? (info.Town, info.Owner) : (null, null);
                return new(name, type, town, owner, state?.GetBool("is public day") ?? false, state?.GetBool("is public night") ?? false, sells);
            },
            Towns = () => towns ??= popData is { } d ? [.. d.Sites.Select(s => new Meitou.Data.Gameplay.Construction.PlacementTown(s.Town, new(s.Position.X, s.Position.Z)))] : [],
            PlayerFactionId = () => popData is { } d && d.PlayerFaction is >= 0 and var mine && mine < d.Factions.Count ? d.Factions[mine].Id : "",
            DrawGhosts = draws => gpu.Objects?.SetGhosts(draws),
        };
    }

    // A level building's instance id and owning faction id (its state's owner, else its town's faction), by placement key; made on first use.
    Dictionary<string, (string Instance, string? Owner)>? levelOwners;

    /// <summary>The floor a ghost stands on in <paramref name="room"/>: a player site's, else a level building's with whether the player's faction owns it.</summary>
    Building.GhostFloor GhostFloorOf(InteriorVolume room, int floor)
    {
        if (shownBuildings.ContainsKey(room.PlacementId)) return new(room.PlacementId, room.Footprint, floor);
        if (levelOwners is null && popData is { } d)
        {
            levelOwners = new(StringComparer.Ordinal);
            foreach (var site in d.Sites)
            {
                string? townOwner = site.Index < d.SiteFactions.Count && d.SiteFactions[site.Index] is >= 0 and var f && f < d.Factions.Count ? d.Factions[f].Id : null;
                foreach (var b in site.Buildings) levelOwners[b.Key] = (b.InstanceId, b.OwnerFactionId ?? townOwner);
            }
        }
        string player = popData is { } p && p.PlayerFaction is >= 0 and var mine && mine < p.Factions.Count ? p.Factions[mine].Id : "";
        return levelOwners?.TryGetValue(room.PlacementId, out var level) == true
            ? new(room.PlacementId, room.Footprint, floor, level.Instance, session.CurrentSnapshot.OwnedTownBuildings.Contains(room.PlacementId) || player.Length > 0 && level.Owner == player)
            : new(room.PlacementId, room.Footprint, floor, room.PlacementId);
    }
}
