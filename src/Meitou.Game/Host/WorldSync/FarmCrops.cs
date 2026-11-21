using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Production;

namespace Meitou.Game.Host.WorldSync;

/// <summary>
/// The crops of the farms (docs/game/buildings-production.md "Farm plants"): each farm machine of the snapshot, the player's sites' and the towns'
/// (<see cref="ProductionSystem.IsTownWork"/>: a town building's, or an object of a house's layout, a hydroponic farm inside), is laid out once per
/// building (positions, meshes, seeded by its id) and its plants' transforms follow its <see cref="FarmView.PlantGrowth"/> and death whenever the
/// machines change.
/// </summary>
sealed class FarmCrops(GameHost host)
{
    /// <summary>
    /// Where a farm's building stands: its site id, BUILDING record, position and rotation, and the building it was placed in (a player's
    /// hydroponic farm indoors), null for none; a house's layout object instead names its house (<paramref name="House"/>) with its floor and
    /// whether it shows only in the interior view, as the house's furniture is drawn.
    /// </summary>
    readonly record struct FarmPlace(string Id, string Record, Vector3 Position, Quaternion Rotation, string? Container = null, string? House = null,
        int Floor = 0, bool InteriorOnly = false);

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
    /// The objects of the towns' buildings' layouts by <see cref="TownLayoutBuilding.Id"/>, made with <see cref="_townBuildings"/>.
    /// </summary>
    Dictionary<string, TownLayoutBuilding>? _townPieces;
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
            if (machine.Farm is not { } view) continue;

            // A farm whose place or layout is not ready on the host yet (its shown building, the towns' lists) is tried again next frame: the
            // machines' version stays put while nothing they show changes.
            if (PlaceOf(machine.Site) is not { } site)
            {
                _farmsVersion = (-1, -1);
                continue;
            }

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
    /// Where the farm of a machine's site stands: a player's site shown, else a town building's or a house's layout object's; null for none.
    /// </summary>
    FarmPlace? PlaceOf(string id)
    {
        if (host.BuildingSync.ShownBuildings.TryGetValue(id, out var site))
            return new(site.Id, site.Record, site.Position, site.Rotation, site.Parent);
        if (!ProductionSystem.IsTownWork(id) || !IndexTowns()) return null;

        string key = id[ProductionSystem.TownWorkPrefix.Length..];
        if (_townBuildings!.TryGetValue(key, out var b))
            return new(id, b.BuildingId, b.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, b.Yaw));
        if (!_townPieces!.TryGetValue(key, out var piece)) return null;

        return new(id, piece.RecordId, piece.Position, piece.Rotation, House: piece.House, Floor: piece.Floor, InteriorOnly: piece.InteriorOnly);
    }

    /// <summary>
    /// The towns' buildings and their layout objects by key, over the towns' lists (made again when they change); false without towns.
    /// </summary>
    bool IndexTowns()
    {
        if (host.PopData is not { } data) return false;
        if (_townBuildings is not null && _townBuildingsRevision == data.BuildingsRevision) return true;

        _townBuildingsRevision = data.BuildingsRevision;
        _townBuildings = new(StringComparer.Ordinal);
        _townPieces = new(StringComparer.Ordinal);
        foreach (var town in data.Sites)
        {
            foreach (var b in town.Buildings)
            {
                _townBuildings[b.Key] = b;
                foreach (var piece in b.LayoutBuildings) _townPieces[piece.Id] = piece;
            }
        }

        return true;
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
        if (plants.Meshes.Length == 0) return null;

        // One material for the whole farm: the first plant's first mesh's part's (the game draws every plant as one mesh with it).
        var material = data.Parts[plants.Meshes[0].Part].Material;
        var meshes = plants.Meshes
            .Select(m => PlantMesh(data.Parts[m.Part], building, site, material))
            .ToArray();
        float height = data.Parts.Max(p => objects.MeshTop(p.Mesh));
        return new FarmLook(site, data, plants, meshes, height);
    }

    /// <summary>
    /// A plant's mesh as a part of its farm building, with the farm's material; inside another building it goes with that one's interior, and in a
    /// house's layout with the house's furniture (its floor, the interior view, the house as its parent placement).
    /// </summary>
    static PlacedMesh PlantMesh(FarmPart part, Data.GameRecord building, FarmPlace site, Data.GameRecord? material) =>
        new(part.Mesh, Matrix4x4.Identity, part.Record, building, site.Id, PlacedKind.BuildingPart)
        {
            Material = material,
            FarmPlant = true,
            Contained = site.Container is not null,
            ContainerId = site.Container,
            Floor = site.Floor,
            Interior = site.InteriorOnly,
            ParentPlacementId = site.House,
        };

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