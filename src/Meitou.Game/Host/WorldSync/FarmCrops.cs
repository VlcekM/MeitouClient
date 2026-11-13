using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Production;

namespace Meitou.Game.Host.WorldSync;

/// <summary>
/// The crops of the farms (docs/game/buildings-production.md "Farm plants"): each farm machine of the snapshot, the player's sites' and the towns'
/// (<see cref="ProductionSystem.IsTownWork"/>), is laid out once per building (positions, meshes, seeded by its id) and its plants' transforms follow
/// its <see cref="FarmView.PlantGrowth"/> and death whenever the machines change.
/// </summary>
sealed class FarmCrops(GameHost host)
{
    /// <summary>
    /// Where a farm's building stands: its site id, BUILDING record, position and rotation.
    /// </summary>
    readonly record struct FarmPlace(string Id, string Record, Vector3 Position, Quaternion Rotation);

    /// <summary>
    /// A farm's crops as laid out once for its building: the place, its farm data, the plant layout and meshes, and what was last handed to
    /// the renderer.
    /// </summary>
    sealed class FarmLook(FarmPlace site, FarmData data, FarmPlants plants, PlacedMesh[] meshes, float height)
    {
        /// <summary>
        /// The place as it was when laid out: a farm moved or with another record is laid out again.
        /// </summary>
        public readonly FarmPlace Site = site;
        /// <summary>
        /// The farm's FARM data (its parts and growth look).
        /// </summary>
        public readonly FarmData Data = data;
        /// <summary>
        /// The plants' positions and which part and random value each mesh has (<see cref="FarmLayout.Plant"/>).
        /// </summary>
        public readonly FarmPlants Plants = plants;
        /// <summary>
        /// One placed mesh per plant mesh, in <see cref="FarmPlants.Meshes"/>' order.
        /// </summary>
        public readonly PlacedMesh[] Meshes = meshes;
        /// <summary>
        /// The top of the tallest part mesh, for <see cref="FarmLayout.Transform"/>.
        /// </summary>
        public readonly float Height = height;
        /// <summary>
        /// Each mesh's transform last handed to the renderer (<see cref="FarmLayout.Transform"/>).
        /// </summary>
        public readonly Matrix4x4?[] Transforms = new Matrix4x4?[meshes.Length];
        /// <summary>
        /// Each mesh's tint last handed to the renderer (<see cref="FarmLayout.Tint"/>).
        /// </summary>
        public readonly Vector3[] Tints = new Vector3[meshes.Length];
        /// <summary>
        /// The growth, death and harvest count the transforms were made for; null until first shown.
        /// </summary>
        public (float Growth, float Death, int Harvested)? Shown;
    }

    /// <summary>
    /// The farms laid out, by site id.
    /// </summary>
    readonly Dictionary<string, FarmLook> _farms = new(StringComparer.Ordinal);
    /// <summary>
    /// Each record's FARM data, null for one without (read once).
    /// </summary>
    readonly Dictionary<string, FarmData?> _farmData = new(StringComparer.Ordinal);
    /// <summary>
    /// The towns' buildings by run-time key (<see cref="TownBuildingOf"/>), and the revision of their lists it was made from.
    /// </summary>
    Dictionary<string, TownBuilding>? _townBuildings;
    /// <summary>
    /// See <see cref="_townBuildings"/>.
    /// </summary>
    int _townBuildingsRevision = -1;
    /// <summary>
    /// The snapshot's machines version and buildings revision last laid out; (-1, -1) lays out the next snapshot.
    /// </summary>
    (long Machines, long Buildings) _farmsVersion = (-1, -1);

    /// <summary>
    /// Once a frame after <see cref="BuildingSync.UpdateBuildings"/>.
    /// </summary>
    internal void UpdateFarms(WorldSnapshot snapshot)
    {
        if (host.ActiveObjects is not { } objects || host.Scene.Database is not { } db) return;
        var version = (snapshot.MachinesVersion, snapshot.BuildingsRevision);
        if (version == _farmsVersion) return;
        _farmsVersion = version;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var machine in snapshot.Machines)
        {
            if (machine.Farm is not { } view || PlaceOf(machine.Site) is not { } site) continue;
            if (!_farms.TryGetValue(site.Id, out var look) || look.Site != site)
            {
                if (MakeFarm(db, objects, site) is not { } made) continue;
                _farms[site.Id] = look = made;
            }
            seen.Add(site.Id);
            if (look.Shown == (view.Growth, view.Death, view.Harvested)) continue;
            look.Shown = (view.Growth, view.Death, view.Harvested);
            for (int i = 0; i < look.Meshes.Length; i++)
            {
                var sub = look.Plants.Meshes[i];
                float growth = sub.Plant < view.PlantGrowth.Count ? view.PlantGrowth[sub.Plant] : 0;
                look.Transforms[i] = FarmLayout.Transform(look.Data.Parts[sub.Part], look.Plants.Positions[sub.Plant], sub.Random,
                    growth, view.Death, look.Height);
                look.Tints[i] = FarmLayout.Tint(look.Data, look.Data.Parts[sub.Part], sub.Random, view.Death);
            }
            objects.Farms.Set(site.Id, look.Meshes, look.Transforms, look.Tints);
        }
        foreach (string id in _farms.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            objects.Farms.Remove(id);
            _farms.Remove(id);
        }
    }

    /// <summary>
    /// Where the farm of a machine's site stands: a player's site shown, else a town building's; null for neither.
    /// </summary>
    FarmPlace? PlaceOf(string id)
    {
        if (host.BuildingSync.ShownBuildings.TryGetValue(id, out var site)) return new(site.Id, site.Record, site.Position, site.Rotation);
        if (!ProductionSystem.IsTownWork(id) || TownBuildingOf(id[ProductionSystem.TownWorkPrefix.Length..]) is not { } b) return null;

        return new(id, b.BuildingId, b.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, b.Yaw));
    }

    /// <summary>
    /// The town building of a run-time key, over the towns' lists (made again when they change); null for none.
    /// </summary>
    TownBuilding? TownBuildingOf(string key)
    {
        if (host.PopData is not { } data) return null;
        if (_townBuildings is null || _townBuildingsRevision != data.BuildingsRevision)
        {
            _townBuildingsRevision = data.BuildingsRevision;
            _townBuildings = new(StringComparer.Ordinal);
            foreach (var town in data.Sites)
                foreach (var b in town.Buildings) _townBuildings[b.Key] = b;
        }

        return _townBuildings.GetValueOrDefault(key);
    }

    /// <summary>
    /// Lays out a farm (its plants, meshes and height); null when its record has no farm parts or is missing.
    /// </summary>
    FarmLook? MakeFarm(Data.GameDatabase db, Rendering.Objects.WorldObjectRenderer objects, FarmPlace site)
    {
        if (!_farmData.TryGetValue(site.Record, out var data))
            _farmData[site.Record] = data = db.Find(site.Record) is { } r ? MachineData.From(db, r)?.Farm : null;
        if (data is not { Parts.Count: > 0 } || db.Find(site.Record) is not { } building) return null;
        var plants = FarmLayout.Plant(data, site.Position, site.Rotation, host.ActiveGround, WorldObjectLayout.Seed(site.Id));
        var meshes = plants.Meshes.Select(m => data.Parts[m.Part])
            .Select(p => new PlacedMesh(p.Mesh, Matrix4x4.Identity, p.Record, building, site.Id, PlacedKind.BuildingPart) { Material = p.Material })
                .ToArray();
        float height = data.Parts.Max(p => objects.MeshTop(p.Mesh));
        return new FarmLook(site, data, plants, meshes, height);
    }

    /// <summary>
    /// Drops every farm's crops from the renderer (a new world).
    /// </summary>
    internal void ForgetFarms()
    {
        foreach (string id in _farms.Keys) host.ActiveObjects?.Farms.Remove(id);
        _farms.Clear();
        _farmsVersion = (-1, -1);
    }
}