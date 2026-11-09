using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Meitou.Game.Effects;
using Meitou.Simulation;
using Meitou.Simulation.Items;

namespace Meitou.Game.Host.WorldSync;

/// <summary>
/// Loose items on the ground (docs/simulation.md "Ground items"): the snapshot's <see cref="WorldSnapshot.GroundItems"/> are diffed by id whenever
/// <see cref="WorldSnapshot.GroundItemsRevision"/> changes, each one a runtime placement of the world objects (its record's mesh,
/// <c>WorldObjectLayout.Item</c>).
/// </summary>
sealed class GroundItemSync(GameHost host)
{
    #region Shown items

    /// <summary>
    /// The snapshot's <see cref="WorldSnapshot.GroundItemsRevision"/> last diffed; -1 diffs the next snapshot.
    /// </summary>
    long _groundRevision = -1;
    /// <summary>
    /// The ids of the ground items shown (as placements, falling or limbs at rest).
    /// </summary>
    readonly HashSet<int> _shownGround = [];
    /// <summary>
    /// Where each shown ground item is drawn (a falling limb: where it comes to rest), for <see cref="GroundShape"/>.
    /// </summary>
    readonly Dictionary<int, (Vector3 At, Quaternion Rotation, string Key)> _groundPoses = [];

    /// <summary>
    /// A ground item's runtime placement key: the renderer's ground prefix and its id.
    /// </summary>
    static string GroundId(int id) =>
        Rendering.Objects.WorldObjectRenderer.GroundKeyPrefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Once a frame with <see cref="BuildingSync.UpdateBuildings"/>, after a new world's runtime placements were cleared.
    /// </summary>
    internal void UpdateGroundItems(WorldSnapshot snapshot)
    {
        host.Session.World.GroundItems.Limit = host.Config.GroundItemLimit;   // the setting, applied at the next drop
        if (snapshot.GroundItemsRevision == _groundRevision) return;
        _groundRevision = snapshot.GroundItemsRevision;
        var current = snapshot.GroundItems.Select(g => g.Id).ToHashSet();
        foreach (int id in _shownGround.Where(id => !current.Contains(id)).ToArray())
        {
            host.ActiveObjects?.RemoveRuntime(GroundId(id));
            _shownGround.Remove(id);
            host.Debris.Falls.RemoveAll(f => f.Ground.Id == id);
            host.Debris.RestLimbs.Remove(id);
            _groundPoses.Remove(id);
            HiddenGround.Remove(id);
            HiddenGroundKeys.Remove(GroundId(id));
            _groundFloors.Remove(id);
        }
        foreach (var g in snapshot.GroundItems)
        {
            if (!_shownGround.Add(g.Id)) continue;
            var (rotation, y) = g.Dropped ? LaidFlat(g) : (g.Rotation, g.Position.Y);
            _groundPoses[g.Id] = (new Vector3(g.Position.X, y, g.Position.Z), rotation, GroundId(g.Id));
            if (host.Debris.LimbLookOf(g.Record) is not null)
            {
                // A severed limb is drawn by the debris renderer (its SeveredLimb look: skin tone and blood), falling and at rest.
                if (g.Fall is { } cut && StartFall(g, cut, rotation, y)) continue;
                host.Debris.RestLimb(g, rotation, y);
                continue;
            }
            if (g.Fall is { } stump && StartFall(g, stump, rotation, y)) continue;   // shown once it has landed (UpdateFalls)
            ShowGround(g, rotation, y);
        }
    }

    /// <summary>
    /// The placement of a ground item: its record's mesh at its place, turned by <paramref name="rotation"/> and set at height <paramref name="y"/>.
    /// </summary>
    internal void ShowGround(GroundItemSnapshot g, Quaternion rotation, float y) =>
        host.ActiveObjects?.AddRuntime(new BuildingPlacement(WorldLayout.ZoneOf(g.Position.X, g.Position.Z), GroundId(g.Id), g.Record, g.Position,
            rotation, null, y) { Key = GroundId(g.Id) });

    /// <summary>
    /// Drops every shown ground item, falling limb, limb at rest and pose (a new world) and diffs the next snapshot afresh.
    /// </summary>
    internal void ForgetGroundItems()
    {
        _shownGround.Clear();
        host.Debris.Falls.Clear();
        host.Debris.RestLimbs.Clear();
        _groundPoses.Clear();
        HiddenGround.Clear();
        HiddenGroundKeys.Clear();
        _groundFloors.Clear();
        _groundRevision = -1;
    }

    #endregion

    #region Laying down

    /// <summary>
    /// Each record's item mesh bounds, by record id (<see cref="BoundsOf"/>); null for one without.
    /// </summary>
    readonly Dictionary<string, OgreBounds?> _groundBounds = [];

    /// <summary>
    /// The bounds of a record's item mesh (its ground mesh, else its mesh), read once; null when it has none.
    /// </summary>
    OgreBounds? BoundsOf(string record)
    {
        if (_groundBounds.TryGetValue(record, out var bounds)) return bounds;
        bounds = null;
        if (host.Scene.Database?.Find(record) is { } item && host.Assets.Find(WorldObjectLayout.ItemMesh(item)) is { } file)
        {
            try
            {
                bounds = OgreMeshReader.TryReadBounds(file, out var b) ? b : OgreMeshReader.ReadFile(file).Bounds;
            }
            catch (Exception e) when (e is IOException or OgreFormatException)
            {
            }
        }
        return _groundBounds[record] = bounds;
    }

    /// <summary>
    /// A dropped item as it would come to rest (engine choice; the original lets a physics body settle it): its mesh's thinnest bounds axis turned
    /// upright, then its yaw, then tilted to the slope of the terrain under it, and set down so that no point of its bounds is below the ground at
    /// that point. A weapon (modelled along +Y) lies on the flat of its blade; a limb on a slope lies along it instead of hanging by its downhill
    /// end. Where the simulation's ground is not the terrain (a floor of a building) the ground is flat at the drop point.
    /// </summary>
    (Quaternion Rotation, float Y) LaidFlat(GroundItemSnapshot g)
    {
        var yaw = new Quaternion(0, g.Rotation.Y, 0, g.Rotation.W);
        if (BoundsOf(g.Record) is not { } box) return (yaw, g.Position.Y + 0.5f);
        var size = box.Max - box.Min;
        var flat = size.X <= size.Y && size.X <= size.Z ? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)   // X up
            : size.Z < size.Y ? Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2)                           // Z up
            : Quaternion.Identity;
        var rotation = Quaternion.Normalize(Quaternion.Concatenate(flat, yaw));
        var at = g.Position;
        // The terrain only where the drop point lies on it (a building's floor is flat for this).
        bool onTerrain = MathF.Abs(host.ActiveGround(at.X, at.Z) - at.Y) < 3;
        float Ground(float x, float z) => onTerrain ? host.ActiveGround(x, z) : at.Y;
        float reach = MathF.Max(MathF.Max(size.X, size.Z) * 0.5f, 1);
        var slope = Vector3.Normalize(new Vector3(Ground(at.X - reach, at.Z) - Ground(at.X + reach, at.Z), 2 * reach,
            Ground(at.X, at.Z - reach) - Ground(at.X, at.Z + reach)));
        float tilt = MathF.Acos(Math.Clamp(slope.Y, -1, 1));
        if (tilt > 1e-3f)
            rotation = Quaternion.Normalize(Quaternion.Concatenate(rotation,
                Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(Vector3.UnitY, slope)), tilt)));
        // Every point of a lattice over its bounds must be at or above the ground below it (a limb on a crest touches the crest, not only the dips
        // round it).
        float y = float.MinValue;
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
                for (int k = 0; k < 5; k++)
                {
                    var local = box.Min + size * new Vector3(i, j, k) / 4;
                    var w = Vector3.Transform(local, rotation);
                    y = MathF.Max(y, Ground(at.X + w.X, at.Z + w.Z) - w.Y);
                }
        return (rotation, y);
    }

    /// <summary>
    /// A severed limb drops from its stump (docs/simulation.md "Ground items", docs/formats/collision.md "Item physics"): <see
    /// cref="Debris.UpdateFalls"/> lets it fall from the stump's bone, sampled once its character is drawn, with gravity from rest, drifts it across
    /// to where the simulation put it and tips it flat on landing. Engine choice: the drift and the hanging pose are not the original's physics.
    /// False when the item has no mesh to fall with (it is then shown at rest).
    /// </summary>
    internal bool StartFall(GroundItemSnapshot g, FallSource stump, Quaternion lying, float y)
    {
        if (host.Scene.Database?.Find(g.Record) is not { } record || WorldObjectLayout.ItemMesh(record) is not { Length: > 0 } mesh) return false;
        if (host.Gpu.Debris?.Extent(mesh, null, host.Debris.LimbLookOf(g.Record)) is null)
            return false;   // the debris renderer draws only meshes it has loaded
        // Hanging: the mesh's long axis down, turned by the item's yaw (the same yaw the rest pose keeps).
        var yaw = new Quaternion(0, g.Rotation.Y, 0, g.Rotation.W);
        var hang = BoundsOf(g.Record) is { } box ? Debris.Turn(LongAxis(box.Max - box.Min), -Vector3.UnitY) : Quaternion.Identity;
        host.Debris.Falls.Add(new Debris.FallingLimb
        {
            Ground = g,
            Mesh = mesh,
            Look = Debris.ToneOf(host.Debris.LimbLookOf(g.Record), g),
            Stump = stump,
            Rest = new Vector3(g.Position.X, y, g.Position.Z),
            RestY = y,
            Hanging = Quaternion.Normalize(Quaternion.Concatenate(hang, yaw)),
            Lying = lying,
        });
        return true;
    }

    /// <summary>
    /// The axis of a box's longest extent (a unit vector).
    /// </summary>
    static Vector3 LongAxis(Vector3 size) => size.X >= size.Y && size.X >= size.Z ? Vector3.UnitX : size.Y >= size.Z ? Vector3.UnitY : Vector3.UnitZ;

    #endregion

    #region Interior floors

    /// <summary>
    /// The ground items above the shown floor of a building in the interior view (game: <c>FUN_1409fb540</c>, as a character there): not picked and
    /// without a label; the renderer leaves their placements out by <see cref="HiddenGroundKeys"/>, a limb's debris is left out here.
    /// </summary>
    internal readonly HashSet<int> HiddenGround = [];

    /// <summary>
    /// The placement keys of <see cref="HiddenGround"/>, the renderer's <c>WorldObjectRenderer.HiddenItems</c>: drawing and picking share one answer.
    /// </summary>
    internal readonly HashSet<string> HiddenGroundKeys = [];

    /// <summary>
    /// By ground item, the building in the view it was last found in, where it was drawn then and the floor it lies on there (<see
    /// cref="UpdateGroundVisibility"/>).
    /// </summary>
    readonly Dictionary<int, (InteriorVolume Building, Vector3 At, int Floor)> _groundFloors = [];

    /// <summary>
    /// After the interior view each frame: which ground items are above their building's shown floor (as <see
    /// cref="InteriorView.AboveViewedFloor"/>). An item's floor is worked out again only when it is drawn somewhere else or found in another
    /// building: a still item stays on its floor, only the shown floor changes.
    /// </summary>
    internal void UpdateGroundVisibility()
    {
        HiddenGround.Clear();
        HiddenGroundKeys.Clear();
        foreach (var (id, pose) in _groundPoses)
        {
            if (host.Interiors.ViewedAt(pose.At) is not { } inView) continue;
            int shown = host.Interiors.InteriorFloors[inView.PlacementId];
            if (shown >= 3) continue;
            if (!_groundFloors.TryGetValue(id, out var known) || !ReferenceEquals(known.Building, inView) || known.At != pose.At)
                _groundFloors[id] = known = (inView, pose.At, inView.FloorAt(pose.At));
            if (known.Floor > shown && HiddenGround.Add(id)) HiddenGroundKeys.Add(pose.Key);
        }
    }

    #endregion

    #region Picking

    /// <summary>
    /// Each record's collision mesh for <see cref="GroundShape"/>, by record and whether its physics file is used; read once.
    /// </summary>
    readonly Dictionary<(string Record, bool File), IReadOnlyList<(Vector3[] Vertices, int[] Indices)>> _groundShapes = [];
    /// <summary>
    /// What <see cref="GroundCollisionOf"/> read of each record, by record id.
    /// </summary>
    readonly Dictionary<string, (bool Weapon, string? Xml)?> _groundCollision = [];

    /// <summary>
    /// What the pointer's ray meets of a ground item, where it is drawn (docs/game/ui-input.md "Building picking and selection"): its record's
    /// collision as the original makes it (<c>FUN_1407626e0</c>, <b>Verified</b>): a weapon's or a severed limb's <c>physics file</c> when that names
    /// an <c>.xml</c>, otherwise its mesh's bounds box. Null without <c>has collision</c> (it is then picked only by its label).
    /// </summary>
    internal (IReadOnlyList<(Vector3[] Vertices, int[] Indices)> Mesh, Vector3 At, Quaternion Rotation)? GroundShape(GroundItemSnapshot g)
    {
        if (!_groundPoses.TryGetValue(g.Id, out var pose) || GroundCollisionOf(g.Record) is not { } collision) return null;
        bool file = collision.Xml is not null && (collision.Weapon || g.Fall is not null);
        if (!_groundShapes.TryGetValue((g.Record, file), out var mesh))
        {
            mesh = file ? host.Player.Build.BuildHost?.Placement.CollisionFile(collision.Xml!) ?? [] : [];
            if (mesh.Count == 0 && BoundsOf(g.Record) is { } box)
                mesh = Box(box.Min, box.Max);   // engine choice: a collision file missing from the install falls back to the box
            _groundShapes[(g.Record, file)] = mesh;
        }
        return mesh.Count == 0 ? null : (mesh, pose.At, pose.Rotation);
    }

    /// <summary>
    /// What <see cref="GroundShape"/> reads of a record, once: whether it is a WEAPON and its <c>physics file</c> when that names an <c>.xml</c>;
    /// null without <c>has collision</c>.
    /// </summary>
    (bool Weapon, string? Xml)? GroundCollisionOf(string record)
    {
        if (_groundCollision.TryGetValue(record, out var known)) return known;
        if (host.Scene.Database?.Find(record) is not { } item) return null;   // not kept: the database may not be loaded yet
        string physics = item.GetPath("physics file");
        return _groundCollision[record] = item.GetBool("has collision")
            ? (item.Type == Data.Fcs.FcsRecordType.Weapon, physics.Contains(".xml") ? physics : null) : null;
    }

    /// <summary>
    /// The twelve triangles of a box.
    /// </summary>
    static IReadOnlyList<(Vector3[] Vertices, int[] Indices)> Box(Vector3 min, Vector3 max)
    {
        var v = new Vector3[8];
        for (int i = 0; i < 8; i++) v[i] = new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
        return [(v, [0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3])];
    }

    #endregion
}