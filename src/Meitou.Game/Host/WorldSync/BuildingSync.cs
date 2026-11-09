using System.Diagnostics;
using System.Numerics;
using Meitou.Data.Gameplay.Combat;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Game.Rendering.Objects;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Population;

namespace Meitou.Game.Host.WorldSync;

/// <summary>
/// The host's side of player construction (docs/simulation.md "Buildings", "Host seam: snapshot diff"): the snapshot's <see
/// cref="WorldSnapshot.Buildings"/> are diffed by id whenever <see cref="WorldSnapshot.BuildingsRevision"/> changes. A new site goes into the
/// renderer's runtime placements and, when it qualifies, the navmesh overlay; a removed one leaves both; a completed one enters the navmesh and
/// lights its lamps. The scaffold of a site that is not finished is read from <c>Progress / Target</c> every frame. The loaded nests' buildings (<see
/// cref="WorldSnapshot.NestBuildings"/>) go through the same diff as complete sites.
/// </summary>
sealed class BuildingSync(GameHost host)
{
    World? _buildingsWorld;
    long _buildingsRevision = -1, _nestBuildingsRevision = -1;
    // What the overlays were last told, by instance id.
    internal readonly Dictionary<string, BuildingSnapshot> ShownBuildings = new(StringComparer.Ordinal);
    readonly HashSet<string> _shownNestMeshes = new(StringComparer.Ordinal);
    readonly HashSet<string> _navBuildings = new(StringComparer.Ordinal), _litBuildings = new(StringComparer.Ordinal),
        _effectBuildings = new(StringComparer.Ordinal);
    readonly Dictionary<string, float> _constructionLook = new(StringComparer.Ordinal);
    Dictionary<string, BuildingBuildData>? _buildData;

    /// <summary>
    /// The build fields of every BUILDING, by id (read once from the game database).
    /// </summary>
    internal Dictionary<string, BuildingBuildData> BuildData() =>
        _buildData ??= host.Scene.Database is { } db ? BuildingBuildData.LoadAll(db)
            .GroupBy(b => b.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal) : [];

    /// <summary>
    /// Once a frame, before the world is drawn.
    /// </summary>
    internal void UpdateBuildings()
    {
        if (!ReferenceEquals(_buildingsWorld, host.Session.World))
        {
            // A new world (a loaded save, a new game): the old world's sites go, the new one's first snapshot carries all of its own.
            ForgetBuildings();
            _buildingsWorld = host.Session.World;
            // The old world's level edits leave the level data, the loaded save's own (if any) are put back.
            host.LevelEditorHost.LevelEditsFollowWorld();
            if (host.LevelEditorHost.PendingLevelEdits is { Count: > 0 } edits) host.LevelEditorHost.ReplayLevelEdits(edits);
            host.LevelEditorHost.PendingLevelEdits = null;
        }
        var snapshot = host.Session.CurrentSnapshot;
        host.TownChanges.UpdateRuins(snapshot);
        host.TownChanges.UpdateEmptiedHouses(snapshot);
        if (snapshot.BuildingsRevision != _buildingsRevision || snapshot.NestBuildingsRevision != _nestBuildingsRevision)
        {
            DiffBuildings(snapshot);
            if (snapshot.NestBuildingsRevision != _nestBuildingsRevision) DiffNestMeshes(snapshot);
            _buildingsRevision = snapshot.BuildingsRevision;
            _nestBuildingsRevision = snapshot.NestBuildingsRevision;
        }
        host.CorpseFlies.UpdateCorpseFlies();
        // A lit building's lamps burn at its power share (granted / demand, 1 for one that draws nothing): dark without supply
        // (docs/formats/lights.md "When a light is on").
        if (host.ActiveLamps is { } lamps && _litBuildings.Count > 0)
            foreach (var m in snapshot.Machines)
                if (m.Power is { Kind: Simulation.Production.PowerKind.Consumer } pw && _litBuildings.Contains(m.Site))
                    lamps.SetRuntimePower(m.Site, pw.Demand > 0 ? pw.Granted / pw.Demand : 1);
        host.GroundItemSync.UpdateGroundItems(snapshot);
        UpdateDoors();
        UpdateTurretPoses(snapshot);
        UpdateRotors(snapshot);
        host.FarmCrops.UpdateFarms(snapshot);
        if (host.ActiveObjects is not { } objects) return;
        _constructionLook.Clear();
        foreach (var site in snapshot.Buildings)
            if (!site.Complete && !site.Destroyed) _constructionLook[site.Id] = ConstructionLook(site);
        objects.Construction = _constructionLook.Count == 0 ? null : _constructionLook;
    }

    /// <summary>
    /// The look's <c>f</c> (docs/game/buildings-production.md "Construction look"): (min(progress / target, 1) - threshold) / (1 - threshold),
    /// threshold = clamp(build threshold / 100, 0, 0.95). Below 0 the whole building is scaffold, so it is held at 0.
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
        foreach (var site in snapshot.Buildings)
            if (site.LevelPlacement is not { } level || !site.Destroyed || !host.TownChanges.LevelDestroyed(level))
                current[site.Id] = site;   // a ruin the level already draws as one needs no overlay
        foreach (var site in snapshot.NestBuildings) current[site.Id] = site;   // the loaded nests' buildings, complete
        foreach (string id in ShownBuildings.Keys.Where(id => !current.ContainsKey(id)).ToArray())
        {
            host.ActiveObjects?.RemoveRuntime(id);
            if (_navBuildings.Remove(id)) host.Nav?.RemoveRuntimeBuilding(id);
            if (_litBuildings.Remove(id)) host.ActiveLamps?.RemoveRuntime(id);
            if (_effectBuildings.Remove(id)) host.Gpu?.Particles?.RemoveBuildingEffects(id);
            ShownBuildings.Remove(id);
        }
        foreach (var site in current.Values)
        {
            var placement = Placement(site);
            if (ShownBuildings.TryGetValue(site.Id, out var shown) && shown.Record == site.Record && shown.Position == site.Position
                    && shown.Rotation == site.Rotation && shown.Destroyed == site.Destroyed)
            {
                if (site.Complete && !shown.Complete) AddToNavmesh(site, placement);   // newly complete
                UpdateLamps(site);
                UpdateEffects(site);
                ShownBuildings[site.Id] = site;
                continue;
            }
            if (shown is not null && _navBuildings.Remove(site.Id))
                host.Nav?.RemoveRuntimeBuilding(site.Id);   // moved or changed: out, then in again
            if (shown is not null && _litBuildings.Remove(site.Id)) host.ActiveLamps?.RemoveRuntime(site.Id);
            if (shown is not null && _effectBuildings.Remove(site.Id)) host.Gpu?.Particles?.RemoveBuildingEffects(site.Id);
            host.ActiveObjects?.AddRuntime(placement);
            AddToNavmesh(site, placement);
            UpdateLamps(site);
            UpdateEffects(site);
            ShownBuildings[site.Id] = site;
        }
    }

    /// <summary>
    /// The standing camps' debris meshes (<see cref="WorldSnapshot.NestMeshes"/>) as runtime item placements, by place: the ones gone are taken out,
    /// the new ones put in.
    /// </summary>
    void DiffNestMeshes(WorldSnapshot snapshot)
    {
        var current = new Dictionary<string, NestMesh>(StringComparer.Ordinal);
        foreach (var m in snapshot.NestMeshes) current.TryAdd(NestMeshId(m), m);
        foreach (string id in _shownNestMeshes.Where(id => !current.ContainsKey(id)).ToArray())
        {
            host.ActiveObjects?.RemoveRuntime(id);
            _shownNestMeshes.Remove(id);
        }
        foreach (var (id, m) in current)
        {
            if (!_shownNestMeshes.Add(id)) continue;
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, m.Yaw);
            host.ActiveObjects?.AddRuntime(new BuildingPlacement(WorldLayout.ZoneOf(m.Position.X, m.Position.Z), id, m.Record, m.Position, rotation,
                null, m.Position.Y)
            {
                Key = id, ItemMesh = m.Mesh, ItemScale = m.Scale,
            });
        }
    }

    static string NestMeshId(NestMesh m) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"nestmesh:{m.Mesh}@{m.Position.X:F1},{m.Position.Z:F1}");

    /// <summary>
    /// A building's lamps are lit while it is complete (docs/game/buildings-production.md "Construction tick": completing switches its lights on).
    /// Placed the way the world's are (<see cref="WorldLights.FromPlaced"/>), without interior layouts: a player building has none.
    /// </summary>
    void UpdateLamps(BuildingSnapshot site)
    {
        if (host.ActiveLamps is not { } lamps) return;
        if (!site.Complete)
        {
            if (site.LevelPlacement is not null)
            {
                _litBuildings.Remove(site.Id);
                lamps.SetRuntime(site.Id, []);
            }
            else if (_litBuildings.Remove(site.Id)) lamps.RemoveRuntime(site.Id);
            return;
        }
        if (!_litBuildings.Add(site.Id) || host.Scene.Database is not { } db || db.Find(site.Record) is not { } record) return;
        if (site.LevelPlacement is not null && host.Scene.Objects is { } objects)
        {
            lamps.SetRuntime(site.Id,
                WorldLights.ForBuilding(db, objects.Levels, new(Placement(site), record, site.Position, site.Destroyed), objects.Layouts));
            return;
        }
        var placed = new List<PlacedLight>();
        WorldObjectLayout.Building(db, record, site.Id, site.Position, site.Rotation, new BuildingState { Identity = site.Id }, placed);
        bool indoors = WorldLights.IsIndoors(record, site.Parent is not null);
        lamps.SetRuntime(site.Id, placed.Select(p => WorldLights.FromPlaced(p, interior: false, indoors: indoors)));
    }

    /// <summary>
    /// A building's effects (a campfire's fire; docs/formats/lights.md "Effects on buildings") run while it is complete, as its lamps.
    /// </summary>
    void UpdateEffects(BuildingSnapshot site)
    {
        if (host.Gpu.Particles is not { } particles) return;
        if (!site.Complete)
        {
            if (_effectBuildings.Remove(site.Id)) particles.RemoveBuildingEffects(site.Id);
            return;
        }
        if (!_effectBuildings.Add(site.Id) || host.Scene.Database is not { } db || db.Find(site.Record) is not { } record) return;
        var placed = new List<PlacedEffect>();
        WorldObjectLayout.Building(db, record, site.Id, site.Position, site.Rotation, new BuildingState { Identity = site.Id }, null, placed);
        particles.SetBuildingEffects(site.Id, placed);
    }

    /// <summary>
    /// The render and navigation overlay type of a site (<c>StateId</c> null, <c>WorldY</c> the absolute height, the zone of its position). Its key
    /// is its own id: a player building's id is unique in the world (the simulation's registry reserves every saved id), and the overlays remove it
    /// by that id.
    /// </summary>
    static BuildingPlacement Placement(BuildingSnapshot site) =>
        site.LevelPlacement is { } level
            ? level with { Key = site.Id, WorldY = site.Position.Y, DestroyedOverride = site.Destroyed }
            : new(WorldLayout.ZoneOf(site.Position.X, site.Position.Z), site.Id, site.Record, site.Position, site.Rotation, null,
                site.Position.Y) { Key = site.Id, Contained = site.Parent is not null, ContainerId = site.Parent };

    /// <summary>
    /// Navmesh membership (engine reading of the Observed rebuild on placing): <c>path mode</c> not 0 and the building complete or without a <c>build
    /// threshold</c>.
    /// </summary>
    void AddToNavmesh(BuildingSnapshot site, BuildingPlacement placement)
    {
        if (host.Nav is null || _navBuildings.Contains(site.Id) || !BuildData().TryGetValue(site.Record, out var data)) return;
        if (data.PathMode == 0 || site.LevelPlacement is null && !site.Complete && data.BuildThreshold != 0) return;
        _navBuildings.Add(site.Id);
        host.Nav.AddRuntimeBuilding(placement);   // the zones loaded are built again; UpdateNav sends the RepathCommand when one is ready
    }

    internal void ForgetBuildings()
    {
        host.ActiveObjects?.ClearRuntime();
        host.ActiveLamps?.ClearRuntime();
        foreach (string id in _effectBuildings) host.Gpu?.Particles?.RemoveBuildingEffects(id);   // the world's own building effects stay
        _effectBuildings.Clear();
        _litBuildings.Clear();
        _levelOwners = null;
        host.GroundItemSync.ForgetGroundItems();
        host.TownChanges.ForgetRuins();
        host.TownChanges.ForgetEmptiedHouses();
        host.FarmCrops.ForgetFarms();
        foreach (string id in _navBuildings) host.Nav?.RemoveRuntimeBuilding(id);
        _navBuildings.Clear();
        ShownBuildings.Clear();
        _constructionLook.Clear();
        _buildingsRevision = -1;
        _nestBuildingsRevision = -1;
        _shownNestMeshes.Clear();
        if (host.ActiveObjects is { } objects) objects.Construction = null;
    }

    /// <summary>
    /// What the player's build mode needs (docs/simulation.md "Buildings", BuildMode): the catalog of BUILDINGs, their texts and parts, the placement
    /// services (terrain, collision footprints) and the ghost drawing. Null without a game database.
    /// </summary>
    internal Building.BuildServices? MakeBuildServices()
    {
        if (host.Scene.Database is not { } db) return null;
        var footprints = new Simulation.Navigation.BuildingFootprints(host.Install, db,
            host.Sandbox.IsSandbox || host.Duel.IsDuel || host.Battle.IsBattle ? null : host.Scene.Objects?.Levels);
        var parts = new Dictionary<string, IReadOnlyList<PlacedMesh>>(StringComparer.Ordinal);
        var icons = new BuildingIcons(Path.Combine(host.Install.DataDirectory, "icons", "buildings"));
        IReadOnlyList<PlacementTown>? towns = null;
        // Per level building of a town: the town, its owner's name and where it sits in the town's buildings (a relaid one takes the same place).
        Dictionary<(ZoneCoordinate, string), (string Town, string? Owner, int Site, int Index)>? townOf = null;
        // The towns' building lists change when the level editor places or clears a building (PopulationData.BuildingsRevision).
        int townOfRevision = -1;
        var pickable = new Dictionary<string, bool>(StringComparer.Ordinal);
        // Resource and node buildings are not selectable objects (TownBuildings leaves them out too).
        bool Pickable(string record) => pickable.TryGetValue(record, out bool known) ? known
            : pickable[record] = db.Find(record) is { } r && !r.GetBool("is node") && !r.GetBool("is foliage");
        // The world's resources for the mine and farm readout, remade when a new game or a load replaces the population data. The check and the popup
        // ask for the same ghost each frame: the last answer is kept.
        var machines = new Dictionary<string, MachineData?>(StringComparer.Ordinal);
        (PopulationData Data, ResourceField Field)? field = null;
        (string Record, Vector3 At, PlacementReadout? Readout)? lastReadout = null;
        PlacementReadout? Readout(string record, Vector3 at)
        {
            if (host.PopData is not { } d) return null;
            if (field?.Data != d) (field, lastReadout) = ((d, new ResourceField(d.Db, d.Biomes, d.ResourceNodes)), null);
            if (lastReadout is { } last && last.Record == record && last.At == at) return last.Readout;
            if (!machines.TryGetValue(record, out var machine))
                machines[record] = machine = db.Find(record) is { Type: Data.Fcs.FcsRecordType.Building } r ? MachineData.From(db, r) : null;
            var readout = PlacementRules.Readout(machine, field.Value.Field, at);
            lastReadout = (record, at, readout);
            return readout;
        }
        return new Building.BuildServices
        {
            Menu = new Building.BuildMenu([.. BuildData().Values]),
            ItemName = id => db.Find(id)?.Name ?? id,
            Description = id => db.Find(id)?.GetString("description") ?? "",
            Parts = id =>
            {
                if (parts.TryGetValue(id, out var cached)) return cached;
                return parts[id] = db.Find(id) is { } record
                    ? WorldObjectLayout.Building(db, record, "ghost:" + id, Vector3.Zero, Quaternion.Identity, new BuildingState())
                    : [];
            },
            IconFile = icons.Find,
            Placement = new Building.PlacementServices
            {
                Ground = host.ActiveGround,
                Footprint = footprints.Of,
                CollisionMesh = footprints.Mesh,
                CollisionFile = footprints.File,
                Plot = footprints.Plot,
                PartBounds = footprints.PartBounds,
                LinkedAround = (centre, radius) => footprints.LinkedAround(centre, radius, (x, z) => host.ActiveGround((float)x, (float)z)),
                StaticAround = footprints.StaticAround,
                StaticPlacementsAround = (centre, radius) => footprints.PlacementsAround(centre, radius).Where(b => Pickable(b.BuildingId)),
                StaticAlong = (origin, direction, length) => footprints.Along(origin, direction, length).Where(b => Pickable(b.Placement.BuildingId)),
                // The movement pick's floors up to the viewed one: the build window's floor arrows choose the floor the ghost stands on.
                FloorAlong = (origin, direction, length) => host.Gpu.Objects?.FloorHit(origin, direction,
                    host.Interiors.ViewedFloorValue, length) is { } hit
                    ? (hit.Position, GhostFloorOf(hit.Room, hit.Room.FloorAt(hit.Position)) with { Ceiling = hit.Room.CeilingAbove(hit.Position), Normal = hit.Room.FloorNormal(hit.Position, host.Interiors.ViewedFloorValue, FloorSurfaces.RayStart) }) : null,
                Readout = Readout,
            },
            WorldInfo = placement =>
            {
                var (zone, instance) = (placement.Zone, placement.InstanceId);
                if ((townOf is null || townOfRevision != host.PopData?.BuildingsRevision) && host.PopData is { } d)
                {
                    townOfRevision = d.BuildingsRevision;
                    townOf = [];
                    foreach (var site in d.Sites)
                    {
                        // A building whose state names no owner belongs to its town's faction (the original re-owns a town's buildings to it).
                        string? townOwner = site.Index < d.SiteFactions.Count && d.SiteFactions[site.Index] is >= 0 and var townFaction
                            && townFaction < d.Factions.Count ? d.Factions[townFaction].Name : null;
                        for (int i = 0; i < site.Buildings.Count; i++)
                        {
                            var b = site.Buildings[i];
                            string? buildingOwner = b.OwnerFactionId is { } id
                                ? d.Factions.FirstOrDefault(f => f.Id == id)?.Name ?? townOwner : townOwner;
                            townOf[(b.Zone, b.InstanceId)] = (site.Town.Name, buildingOwner, site.Index, i);
                        }
                    }
                }
                var levels = host.Scene.Objects?.Levels;
                var state = levels is not null && placement.StateId is { } stateId && levels.Zones.TryGetValue(zone, out var zoneDb)
                    ? zoneDb.Find(stateId) : null;
                var info = townOf is not null && townOf.TryGetValue((zone, instance), out var found) ? found
                    : default((string Town, string? Owner, int Site, int Index)?);
                var building = info is { } at && host.PopData is { } live && at.Site < live.Sites.Count
                    && at.Index < live.Sites[at.Site].Buildings.Count
                    ? live.Sites[at.Site].Buildings[at.Index] : null;
                var saved = host.Session.World.Fixtures.Saved?.GetValueOrDefault(placement.Key);
                var (name, type) = WorldBuildingName(db, placement, state, saved, building?.Key == placement.Key ? building : null);
                var (town, owner) = info is { } t ? (t.Town, t.Owner) : (null, null);
                return new(name, type, town, owner, state?.GetBool("is public day") ?? false, state?.GetBool("is public night") ?? false,
                    host.TownChanges.ShownRuins.TryGetValue(placement.Key, out var ruin) ? ruin.Destroyed
                        : placement.DestroyedOverride ?? state?.GetBool("destroyed") ?? false);
            },
            Towns = () => towns ??= host.PopData is { } d ? [.. d.Sites.Select(s => new PlacementTown(s.Town, new(s.Position.X, s.Position.Z)))] : [],
            PlayerFactionId = () => host.PopData is { } d && d.PlayerFaction is >= 0 and var mine && mine < d.Factions.Count ? d.Factions[mine].Id
                : "",
            DrawGhosts = draws => host.ActiveObjects?.SetGhosts(draws),
        };
    }

    /// <summary>
    /// A world building's name and its <c>Type:</c> (the record's name when that differs), as the game names it in both UI modes: read from the
    /// naming a town override's housing gave <paramref name="building"/>, else <paramref name="saved"/>, a loaded save's state, else <paramref
    /// name="state"/>, the level state the boot's housing named (ResidentRuins).
    /// </summary>
    static (string Name, string? Type) WorldBuildingName(Data.GameDatabase db, BuildingPlacement placement,
        Data.GameRecord? state, Data.Fcs.FcsRecord? saved, TownBuilding? building)
    {
        string StateText(string key) => building?.Naming is { } naming && key is ResidentRuins.NameKey or ResidentRuins.SquadTemplateKey
            ? key == ResidentRuins.NameKey ? naming.Name : naming.SquadTemplate
            : saved is not null && saved.Strings.TryGetValue(key, out var text) ? text : state?.GetString(key) ?? "";
        string recordName = db.Find(placement.BuildingId)?.Name ?? placement.BuildingId;

        // The squad template that runs it names a special building ("The Dancing Skeleton").
        var squad = StateText(ResidentRuins.SquadTemplateKey) is { Length: > 0 } squadId ? db.Find(squadId) : null;
        string name = squad?.GetString("building name") is { Length: > 0 } squadName ? squadName
            : StateText(ResidentRuins.NameKey) is { Length: > 0 } own ? own : recordName;
        return (name, name != recordName ? recordName : null);
    }

    // A level building's instance id, owning faction id (its state's owner, else its town's faction) and town building, by placement key; made on
    // first use.
    Dictionary<string, (string Instance, string? Owner, TownBuilding Building)>? _levelOwners;
    int _levelOwnersRevision = -1;

    /// <summary>
    /// The floor a ghost stands on in <paramref name="room"/>: a player site's, else a level building's with what <c>PlacementRules.MayBuildIn</c>
    /// reads (whether the player's faction owns it, whether it is a ruin, whether its owner is <c>not real</c>). A repaired town house is shown as a
    /// site but stays a level building, so what is built in it names the level instance as its parent (as in any owned town building). A level
    /// building of no town has no owner: the original's "nofac", which is not real.
    /// </summary>
    Building.GhostFloor GhostFloorOf(InteriorVolume room, int floor)
    {
        if (ShownBuildings.TryGetValue(room.PlacementId, out var shown) && shown.LevelPlacement is null)
            return new(room.PlacementId, room.Footprint, floor);
        if (LevelOwners()?.TryGetValue(room.PlacementId, out var level) != true)
            return new(room.PlacementId, room.Footprint, floor, room.PlacementId) { OwnerNotReal = true };

        string player = host.PopData is { } p && p.PlayerFaction is >= 0 and var mine && mine < p.Factions.Count ? p.Factions[mine].Id : "";
        bool owned = host.Session.CurrentSnapshot.OwnedTownBuildings.Contains(room.PlacementId) || player.Length > 0 && level.Owner == player;
        return new(room.PlacementId, room.Footprint, floor, level.Instance, owned)
        {
            Destroyed = host.TownChanges.ShownRuins.TryGetValue(room.PlacementId, out var ruin) ? ruin.Destroyed
                : host.Session.World.Fixtures.SavedDestroyed(level.Building),
            OwnerNotReal = level.Owner is not { } owner || host.PopData?.Factions.FirstOrDefault(f => f.Id == owner) is not { NotReal: false },
        };
    }

    Dictionary<string, (string Instance, string? Owner, TownBuilding Building)>? LevelOwners()
    {
        if (host.PopData is not { } d || _levelOwners is not null && _levelOwnersRevision == d.BuildingsRevision) return _levelOwners;

        _levelOwnersRevision = d.BuildingsRevision;
        _levelOwners = new(StringComparer.Ordinal);
        foreach (var site in d.Sites)
        {
            string? townOwner = site.Index < d.SiteFactions.Count && d.SiteFactions[site.Index] is >= 0 and var f && f < d.Factions.Count
                ? d.Factions[f].Id : null;
            foreach (var b in site.Buildings) _levelOwners[b.Key] = (b.InstanceId, b.OwnerFactionId ?? townOwner, b);
        }
        return _levelOwners;
    }

    // <summary>The doors' open amounts from the snapshot to the object renderer (docs/simulation.md "Cages, beds and locks"), made again only when
    // the snapshot's door table is a new one.</summary>
    IReadOnlyDictionary<(string Building, int Door), DoorView>? _shownDoors;

    /// <summary>
    /// Once a frame, with the buildings.
    /// </summary>
    void UpdateDoors()
    {
        if (host.ActiveObjects is not { } objects) return;
        var doors = host.Session.CurrentSnapshot.Doors;
        if (ReferenceEquals(doors, _shownDoors)) return;
        _shownDoors = doors;
        var open = new Dictionary<(string Placement, int Door), float>(doors.Count);
        foreach (var (key, view) in doors)
            if (view.Open > 0) open[key] = view.Open;
        objects.Doors = open.Count == 0 ? null : open;
    }

    // The parts that turn by themselves (WorldObjectRenderer.AdvanceRotors, docs/game/buildings-production.md "Rotating parts"): each frame the
    // object renderer is told the scheduled wind and what each building's ROTATION_OUTPUT parts turn by, then moves them by the frame's game time
    // (real time x speed, 0 while paused). Display only.
    readonly Dictionary<string, float> _rotorOutputs = new(StringComparer.Ordinal);
    readonly Dictionary<string, float> _levelRotorOutputs = new(StringComparer.Ordinal);
    readonly HashSet<string> _unbuiltSites = new(StringComparer.Ordinal);
    readonly Stopwatch _rotorClock = new();

    /// <summary>
    /// Once a frame, with the buildings.
    /// </summary>
    void UpdateRotors(WorldSnapshot snapshot)
    {
        if (host.Gpu?.Objects is not { } objects) return;
        _rotorOutputs.Clear();
        foreach (var m in snapshot.Machines)
            if (!m.IsContainer) _rotorOutputs[m.Site] = m.Rotation;
        _unbuiltSites.Clear();
        foreach (var site in snapshot.Buildings)
            if (!site.Complete) _unbuiltSites.Add(site.Id);
        objects.RotorOutput ??= RotorOutputOf;
        objects.RotorUnbuilt ??= placed => _unbuiltSites.Contains(placed.PlacementId);
        objects.RotorWind ??= (x, z) => host.Gpu.Weather?.World is { } weather ? weather.WindAt(x, z) : (0, Vector2.Zero);
        float real = _rotorClock.IsRunning ? (float)Math.Min(_rotorClock.Elapsed.TotalSeconds, 0.25) : 0;
        _rotorClock.Restart();
        var sim = host.Session.Simulation;
        objects.AdvanceRotors(sim.IsPaused ? 0 : real * (float)sim.Speed);
    }

    /// <summary>
    /// A building's ROTATION_OUTPUT share: a player machine's (<see cref="Meitou.Simulation.Production.MachineSnapshot.Rotation"/>); a level
    /// building, which the simulation does not run, by its data as its sound emitter is (engine choice): a generator its output x 0.01, a battery and
    /// a machine that needs neither operators nor power its <c>output rate</c>, any other machine 0, a building without a function 1 (the plain
    /// building's).
    /// </summary>
    float RotorOutputOf(PlacedMesh placed)
    {
        if (_rotorOutputs.TryGetValue(placed.PlacementId, out float share)) return share;
        string id = placed.Owner.StringId;
        if (_levelRotorOutputs.TryGetValue(id, out share)) return share;
        var data = host.Scene.Database is { } db ? MachineData.From(db, placed.Owner) : null;
        share = data is null ? 1
            : data.IsGenerator ? data.PowerOutput * 0.01f
            : data.IsBattery || (data.PowerOperators == 0 && data.PowerOutput == 0) ? data.OutputRate
            : 0;
        _levelRotorOutputs[id] = share;
        return share;
    }

    // The turrets' turning parts from the snapshot to the object renderer (WorldObjectRenderer.Turrets, docs/simulation.md "Combat", turrets): a
    // manned, simulated turret (MannedTurret.Angles) is drawn with its parts turned by the angles of the last two snapshots, interpolated by the
    // simulation's alpha. Display only. A turret keeps its last turn when its gunner leaves (the building keeps the turn, as the simulation's turret
    // does); a world change drops them all.
    readonly Dictionary<string, TurretPose> _turretPoses = new(StringComparer.Ordinal);
    readonly Dictionary<string, float[]> _previousTurretAngles = new(StringComparer.Ordinal);
    // The turrets whose records are missing (not retried).
    readonly HashSet<string> _turretPoseMissing = new(StringComparer.Ordinal);
    World? _turretPosesWorld;

    /// <summary>
    /// Once a frame, with the buildings.
    /// </summary>
    internal void UpdateTurretPoses(WorldSnapshot snapshot)
    {
        if (host.Gpu?.Objects is not { } objects || host.Scene.Database is not { } db) return;
        if (!ReferenceEquals(_turretPosesWorld, host.Session.World))
        {
            _turretPosesWorld = host.Session.World;
            _turretPoses.Clear();
            _turretPoseMissing.Clear();
            objects.Turrets = null;
        }
        _previousTurretAngles.Clear();
        foreach (var c in host.Session.PreviousSnapshot.Characters)
            if (c.Turret is { Simulated: true, Angles: { } before }) _previousTurretAngles[c.Turret.Key] = before;
        float alpha = host.Session.SimulationAlpha;
        bool added = false;
        foreach (var c in snapshot.Characters)
        {
            if (c.Turret is not { Simulated: true, Angles: { } angles } post || _turretPoseMissing.Contains(post.Key)) continue;
            if (!_turretPoses.TryGetValue(post.Key, out var pose))
            {
                if (db.Find(post.BuildingId) is not { } building || TurretGun.From(db, building) is not { } gun)
                {
                    _turretPoseMissing.Add(post.Key);
                    continue;
                }
                _turretPoses[post.Key] = pose = new TurretPose([.. gun.Parts.Select(p => p.Id)]);
                added = true;
            }
            _previousTurretAngles.TryGetValue(post.Key, out var old);
            bool changed = false;
            for (int k = 0; k < pose.Angles.Length && k < angles.Length; k++)
            {
                float shown = old is not null && k < old.Length ? old[k] + (angles[k] - old[k]) * alpha : angles[k];
                if (shown == pose.Angles[k]) continue;
                pose.Angles[k] = shown;
                changed = true;
            }
            if (changed) pose.Version++;
        }
        if (added) objects.Turrets = new Dictionary<string, TurretPose>(_turretPoses, StringComparer.Ordinal);
    }
}