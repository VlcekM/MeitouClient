using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Production;

namespace Meitou.Game.Host.WorldSync;

/// <summary>
/// The crops of the player's farms (docs/game/buildings-production.md "Farm plants"): each farm machine of the snapshot is laid out once per building
/// (positions, meshes, seeded by its id) and its plants' transforms follow its <see cref="FarmView.PlantGrowth"/> and death whenever the machines
/// change.
/// </summary>
sealed class FarmCrops(GameHost host)
{
    /// <summary>
    /// A farm's crops as laid out once for its building: the site, its farm data, the plant layout and meshes, and what was last handed to
    /// the renderer.
    /// </summary>
    sealed class FarmLook(BuildingSnapshot site, FarmData data, FarmPlants plants, PlacedMesh[] meshes, float height)
    {
        /// <summary>
        /// The site as it was when laid out: a farm moved or with another record is laid out again.
        /// </summary>
        public readonly BuildingSnapshot Site = site;
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
            if (machine.Farm is not { } view || !host.BuildingSync.ShownBuildings.TryGetValue(machine.Site, out var site)) continue;
            if (!_farms.TryGetValue(site.Id, out var look) || look.Site.Record != site.Record || look.Site.Position != site.Position
                    || look.Site.Rotation != site.Rotation)
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
    /// Lays out a site's farm (its plants, meshes and height); null when its record has no farm parts or is missing.
    /// </summary>
    FarmLook? MakeFarm(Data.GameDatabase db, Rendering.Objects.WorldObjectRenderer objects, BuildingSnapshot site)
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