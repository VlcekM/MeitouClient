using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>One object of a building layout: a BUILDING placed relative to the building the layout is used on.</summary>
/// <param name="InstanceId">The instance id inside the layout's INSTANCE_COLLECTION.</param>
/// <param name="Position">Position in the parent building's frame (its position and rotation, no scale).</param>
/// <param name="Rotation">Rotation relative to the parent building.</param>
public sealed record LayoutItem(string InstanceId, GameRecord Building, Vector3 Position, Quaternion Rotation)
{
    /// <summary>The floor the object stands on: its state's <c>floornum</c> (0 when it has none); in the interior view an object above the viewed floor is hidden.</summary>
    public int Floor { get; init; }

    /// <summary>
    /// Its state's outside flag (<c>flags</c> bit 0, else the older bool <c>is outside</c>; docs/formats/zones.md): an interior layout's object without it is hidden while
    /// the building shows its outside, one with it (a yard's tents, torches, benches) stays.
    /// </summary>
    public bool Outside { get; init; }
}

/// <summary>
/// Building layouts from <c>interiors.level</c> (docs/formats/zones.md, "Building layouts"): INSTANCE_COLLECTION records whose
/// <c>associated building</c> names a BUILDING and whose name is what a GAMESTATE_BUILDING's <c>exterior layout name</c> /
/// <c>interior layout name</c> refers to. Exterior layouts (<c>is exterior</c>, ids <c>EXT-&lt;building&gt;-&lt;name&gt;</c>) hold the
/// signs and banners outside a building; interior layouts the furniture inside.
/// </summary>
public sealed class BuildingLayouts
{
    readonly GameDatabase db;
    readonly GameDatabase? interiors;
    // (exterior, associated building) -> layouts of that building.
    readonly Dictionary<(bool Exterior, string Building), List<GameRecord>> byBuilding = [];

    public BuildingLayouts(GameDatabase db, GameDatabase? interiors)
    {
        this.db = db;
        this.interiors = interiors;
        if (interiors is null) return;
        foreach (var r in interiors.OfType(FcsRecordType.INSTANCE_COLLECTION).OrderBy(r => r.StringId, StringComparer.Ordinal))
        {
            var building = r.GetString("associated building");
            if (building.Length == 0) continue;
            var key = (r.GetBool("is exterior"), building);
            if (!byBuilding.TryGetValue(key, out var list)) byBuilding[key] = list = [];
            list.Add(r);
        }
    }

    /// <summary>Layout records indexed, exterior and interior.</summary>
    public int Count => byBuilding.Values.Sum(l => l.Count);

    /// <summary>
    /// The layout named <paramref name="name"/> for <paramref name="building"/>: one associated with the building itself, else with a
    /// building it lists in <c>shares interiors with</c>; within each, an exact name match before a case-insensitive one. Null when
    /// there is none (no fallback to another building's layout of the same name: its positions belong to that building's shape).
    /// </summary>
    public GameRecord? Find(GameRecord building, string name, bool exterior = true)
    {
        if (name.Length == 0) return null;
        foreach (var id in building.GetReferences("shares interiors with").Select(r => r.TargetStringId).Prepend(building.StringId))
        {
            if (!byBuilding.TryGetValue((exterior, id), out var list)) continue;
            var match = list.FirstOrDefault(r => r.Name == name) ?? list.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return null;
    }

    /// <summary>The names of the layouts <see cref="Find"/> can return for <paramref name="building"/> (its own and those of the buildings it shares interiors with).</summary>
    public IEnumerable<string> Names(GameRecord building, bool exterior)
    {
        foreach (var id in building.GetReferences("shares interiors with").Select(r => r.TargetStringId).Prepend(building.StringId))
            if (byBuilding.TryGetValue((exterior, id), out var list))
                foreach (var r in list) yield return r.Name;
    }

    /// <summary>The layout's objects whose target is a BUILDING of the game data, in instance id order.</summary>
    public IEnumerable<LayoutItem> Items(GameRecord layout) => Placed(layout, FcsRecordType.BUILDING);

    /// <summary>
    /// The layout's loose-item slots: its objects whose target is an ITEM_PLACEMENT_GROUP (<see cref="LayoutItem.Building"/> is then the group; docs/game/factions-squads-towns.md
    /// "Loose items in buildings"), in instance id order.
    /// </summary>
    public IEnumerable<LayoutItem> ItemGroups(GameRecord layout) => Placed(layout, FcsRecordType.ITEM_PLACEMENT_GROUP);

    IEnumerable<LayoutItem> Placed(GameRecord layout, FcsRecordType type)
    {
        foreach (var i in layout.Instances.Values.Where(i => !i.IsCleared).OrderBy(i => i.Id, StringComparer.Ordinal))
            if (db.Find(i.Target) is { } b && b.Type == type)
            {
                var state = i.States.Count > 0 ? interiors?.Find(i.States[0]) : null;
                yield return new LayoutItem(i.Id, b, i.Position, i.Rotation)
                {
                    Floor = state is not null ? Math.Clamp(state.GetInt("floornum"), 0, 3) : 0,
                    Outside = state is not null && IsOutside(state),
                };
            }
    }

    /// <summary>
    /// World position and rotation of a layout object on a building at <paramref name="parentPosition"/> with
    /// <paramref name="parentRotation"/>: the object's local transform (rotation, then its position) under the building's node.
    /// </summary>
    public static (Vector3 Position, Quaternion Rotation) Place(Vector3 parentPosition, Quaternion parentRotation, LayoutItem item)
    {
        var node = Matrix4x4.CreateFromQuaternion(Normalized(parentRotation)) * Matrix4x4.CreateTranslation(parentPosition);
        var world = Matrix4x4.CreateFromQuaternion(Normalized(item.Rotation)) * Matrix4x4.CreateTranslation(item.Position) * node;
        return (world.Translation, Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(world)));
    }

    /// <summary>A building state's outside flag: <c>flags</c> bit 0, or the older bool <c>is outside</c> without it (docs/formats/zones.md).</summary>
    public static bool IsOutside(GameRecord state) => state.Ints.TryGetValue("flags", out int flags) ? (flags & 1) != 0 : state.GetBool("is outside");

    static Quaternion Normalized(Quaternion q) => q.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(q);
}
