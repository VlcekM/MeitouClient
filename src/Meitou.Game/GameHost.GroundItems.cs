using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Items;

namespace Meitou.Game;

/// <summary>
/// Loose items on the ground (docs/simulation.md "Ground items"): the snapshot's <see cref="WorldSnapshot.GroundItems"/> are diffed by id whenever
/// <see cref="WorldSnapshot.GroundItemsRevision"/> changes, each one a runtime placement of the world objects (its record's mesh, <c>WorldObjectLayout.Item</c>).
/// </summary>
sealed partial class GameHost
{
    long groundRevision = -1;
    readonly HashSet<int> shownGround = [];

    static string GroundId(int id) => "ground:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Once a frame with <see cref="UpdateBuildings"/>, after a new world's runtime placements were cleared.</summary>
    void UpdateGroundItems(WorldSnapshot snapshot)
    {
        session.World.GroundItems.Limit = config.GroundItemLimit;   // the setting, applied at the next drop
        if (snapshot.GroundItemsRevision == groundRevision) return;
        groundRevision = snapshot.GroundItemsRevision;
        var current = snapshot.GroundItems.Select(g => g.Id).ToHashSet();
        foreach (int id in shownGround.Where(id => !current.Contains(id)).ToArray())
        {
            gpu.Objects?.RemoveRuntime(GroundId(id));
            shownGround.Remove(id);
        }
        foreach (var g in snapshot.GroundItems)
        {
            if (!shownGround.Add(g.Id)) continue;
            var (rotation, y) = g.Dropped ? LaidFlat(g) : (g.Rotation, g.Position.Y);
            gpu.Objects?.AddRuntime(new BuildingPlacement(WorldLayout.ZoneOf(g.Position.X, g.Position.Z), GroundId(g.Id), g.Record, g.Position, rotation, null, y) { Key = GroundId(g.Id) });
        }
    }

    readonly Dictionary<string, OgreBounds?> groundBounds = [];

    /// <summary>
    /// A dropped item as it would come to rest (engine choice; the original lets physics settle it): its mesh's thinnest bounds axis turned upright, then its yaw, resting its
    /// lowest corner on the drop point's ground. A weapon (modelled along +Y) lies on the flat of its blade.
    /// </summary>
    (Quaternion Rotation, float Y) LaidFlat(GroundItemSnapshot g)
    {
        var yaw = new Quaternion(0, g.Rotation.Y, 0, g.Rotation.W);
        if (!groundBounds.TryGetValue(g.Record, out var bounds))
        {
            bounds = null;
            if (scene.Database?.Find(g.Record) is { } record && assets.Find(WorldObjectLayout.ItemMesh(record)) is { } file)
            {
                try { bounds = OgreMeshReader.TryReadBounds(file, out var b) ? b : OgreMeshReader.ReadFile(file).Bounds; }
                catch (Exception e) when (e is IOException or OgreFormatException) { }
            }
            groundBounds[g.Record] = bounds;
        }
        if (bounds is not { } box) return (yaw, g.Position.Y + 0.5f);
        var size = box.Max - box.Min;
        var flat = size.X <= size.Y && size.X <= size.Z ? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)   // X up
            : size.Z < size.Y ? Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2)                           // Z up
            : Quaternion.Identity;
        var rotation = Quaternion.Normalize(Quaternion.Concatenate(flat, yaw));
        float lowest = float.MaxValue;
        for (int c = 0; c < 8; c++)
        {
            var corner = new Vector3((c & 1) == 0 ? box.Min.X : box.Max.X, (c & 2) == 0 ? box.Min.Y : box.Max.Y, (c & 4) == 0 ? box.Min.Z : box.Max.Z);
            lowest = MathF.Min(lowest, Vector3.Transform(corner, rotation).Y);
        }
        return (rotation, g.Position.Y - lowest);
    }

    void ForgetGroundItems()
    {
        shownGround.Clear();
        groundRevision = -1;
    }
}
