using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>
/// One object of a building layout: a BUILDING placed relative to the building the layout is used on.
/// </summary>
/// <param name="InstanceId">The instance id inside the layout's INSTANCE_COLLECTION.</param>
/// <param name="Position">Position in the parent building's frame (its position and rotation, no scale).</param>
/// <param name="Rotation">Rotation relative to the parent building.</param>
public sealed record LayoutItem(string InstanceId, GameRecord Building, Vector3 Position, Quaternion Rotation)
{
    /// <summary>
    /// The floor the object stands on: its state's <c>floornum</c> (0 when it has none); in the interior view an object above the viewed floor is
    /// hidden.
    /// </summary>
    public int Floor { get; init; }

    /// <summary>
    /// Its state's outside flag (<c>flags</c> bit 0, else the older bool <c>is outside</c>; docs/formats/zones.md): an interior layout's object
    /// without it is hidden while the building shows its outside, one with it (a yard's tents, torches, benches) stays.
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
    /// <summary>
    /// The game database the layouts' objects are found in.
    /// </summary>
    readonly GameDatabase _db;
    /// <summary>
    /// The <c>interiors.level</c> records; null when there are none.
    /// </summary>
    readonly GameDatabase? _interiors;
    /// <summary>
    /// (exterior, associated building) -> layouts of that building.
    /// </summary>
    readonly Dictionary<(bool Exterior, string Building), List<GameRecord>> _byBuilding = [];
    /// <summary>
    /// The factions' building swaps (<see cref="UseSwaps(Gameplay.BuildingSwaps, IReadOnlyDictionary{string, string})"/>); null when off.
    /// </summary>
    Gameplay.BuildingSwaps? _swaps;
    /// <summary>
    /// The owner faction of each placement by key, for the swaps.
    /// </summary>
    IReadOnlyDictionary<string, string>? _swapOwners;

    /// <summary>
    /// Indexes the layouts of <paramref name="interiors"/> (none when it is null), their objects found in <paramref name="db"/>.
    /// </summary>
    public BuildingLayouts(GameDatabase db, GameDatabase? interiors)
    {
        this._db = db;
        this._interiors = interiors;
        if (interiors is null) return;
        foreach (var r in interiors.OfType(FcsRecordType.InstanceCollection).OrderBy(r => r.StringId, StringComparer.Ordinal))
        {
            var building = r.GetString("associated building");
            if (building.Length == 0) continue;
            var key = (r.GetBool("is exterior"), building);
            if (!_byBuilding.TryGetValue(key, out var list)) _byBuilding[key] = list = [];
            list.Add(r);
        }
    }

    /// <summary>
    /// Layout records indexed, exterior and interior.
    /// </summary>
    public int Count => _byBuilding.Values.Sum(l => l.Count);

    /// <summary>
    /// The layout named <paramref name="name"/> for <paramref name="building"/>, of the building itself, else of a building it lists in
    /// <c>shares interiors with</c>; null when there is none (no fallback to another building's layout of the same name: its positions belong to
    /// that building's shape). An exterior is the record with the string id <c>EXT-&lt;building&gt;-&lt;name&gt;</c>, exactly, as the original's
    /// loader finds it (<c>LayoutExterior</c> vfunc 14 <c>FUN_14054c080</c>, its fallback <c>FUN_14055a730</c>, Verified): neither the record's name
    /// nor its <c>associated building</c> is read, so <c>Mechanical Sign</c> finds no <c>EXT-...-Mechanical SIgn</c>. An interior is one associated
    /// with the building, an exact name match before a case-insensitive one (the original's is by the id <c>&lt;building&gt;-&lt;name&gt;</c>, which
    /// finds the same record for every level-data home).
    /// </summary>
    public GameRecord? Find(GameRecord building, string name, bool exterior = true)
    {
        if (name.Length == 0) return null;

        foreach (var id in Sharing(building))
        {
            var match = exterior ? _interiors?.Find($"{ExteriorPrefix}{id}-{name}") : Interior(id, name);
            if (match is not null) return match;
        }

        return null;
    }

    /// <summary>
    /// The start of an exterior layout's string id, before the building's.
    /// </summary>
    const string ExteriorPrefix = "EXT-";

    /// <summary>
    /// The building's string id, then those it lists in <c>shares interiors with</c>.
    /// </summary>
    static IEnumerable<string> Sharing(GameRecord building) =>
        building.GetReferences("shares interiors with").Select(r => r.TargetStringId).Prepend(building.StringId);

    /// <summary>
    /// The interior layout named <paramref name="name"/> associated with building <paramref name="id"/>: an exact match, else a case-insensitive one.
    /// </summary>
    GameRecord? Interior(string id, string name)
    {
        if (!_byBuilding.TryGetValue((false, id), out var list)) return null;

        return list.FirstOrDefault(r => r.Name == name) ?? list.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The names of the layouts <see cref="Find"/> can return for <paramref name="building"/> (its own and those of the buildings it shares interiors
    /// with), case kept; housing takes the building to offer exactly these (the original compares names exactly, Verified). Interior: the names of
    /// the interior layouts associated with those buildings (<c>FUN_14055ad40</c>). Exterior: the rest of each <c>interiors.level</c> string id
    /// <c>EXT-&lt;building&gt;-&lt;name&gt;</c> of those buildings (<c>FUN_14055b330</c>).
    /// </summary>
    public IEnumerable<string> Names(GameRecord building, bool exterior)
    {
        foreach (var id in Sharing(building))
        {
            foreach (string name in exterior ? ExteriorNames(id) : InteriorNames(id)) yield return name;
        }
    }

    /// <summary>
    /// The names of the interior layouts associated with building <paramref name="id"/>.
    /// </summary>
    IEnumerable<string> InteriorNames(string id) => _byBuilding.TryGetValue((false, id), out var list) ? list.Select(r => r.Name) : [];

    /// <summary>
    /// The layout names of the <c>EXT-&lt;id&gt;-&lt;name&gt;</c> string ids of building <paramref name="id"/>.
    /// </summary>
    IEnumerable<string> ExteriorNames(string id)
    {
        string prefix = $"{ExteriorPrefix}{id}-";

        return _interiors?.Records.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(k => k[prefix.Length..]) ?? [];
    }

    /// <summary>
    /// The layout's objects whose target is a BUILDING of the game data, in instance id order.
    /// </summary>
    public IEnumerable<LayoutItem> Items(GameRecord layout) => Placed(layout, FcsRecordType.Building);

    /// <summary>
    /// Turns on the factions' building swaps for the furniture of <see cref="Items(GameRecord, BuildingPlacement)"/>: <paramref name="owners"/> is
    /// the owner faction of each placement by key (<see cref="BuildingSwapPass"/>).
    /// </summary>
    public void UseSwaps(Gameplay.BuildingSwaps swaps, IReadOnlyDictionary<string, string> owners) => (this._swaps, _swapOwners) = (swaps, owners);

    /// <summary>
    /// Swaps the furniture as <paramref name="levels"/>' placements were swapped (<see cref="WorldLevelData.FurnitureSwaps"/>; nothing when they were
    /// not). Every reader of the layouts calls it, or a piece the owner's swaps skip is drawn by none but still walled off in the navmesh (#345,
    /// #346: a robot faction's beds).
    /// </summary>
    public void UseSwaps(WorldLevelData levels)
    {
        if (levels.FurnitureSwaps is { } s) UseSwaps(s.Swaps, s.Owners);
    }

    /// <summary>
    /// The layout's furniture as <paramref name="placement"/>'s owner faction has it made: each piece goes through the faction's building swaps as
    /// <c>createBuilding</c> asks them (<c>FUN_14057cc70</c>), a skipped one is left out and a replaced one is the new building in the same place.
    /// Without swaps, <see cref="Items(GameRecord)"/>.
    /// </summary>
    public IEnumerable<LayoutItem> Items(GameRecord layout, BuildingPlacement placement)
    {
        if (_swaps is null || _swapOwners is null || !_swapOwners.TryGetValue(placement.Key, out var owner)) return Items(layout);
        return Swapped(Items(layout), owner, placement.Key);
    }

    /// <summary>
    /// <paramref name="items"/> through <paramref name="owner"/>'s swaps: kept, left out, or replaced by the new building in the same place.
    /// </summary>
    IEnumerable<LayoutItem> Swapped(IEnumerable<LayoutItem> items, string owner, string key)
    {
        foreach (var item in items)
        {
            if (_swaps!.Replacement(owner, item.Building.StringId, BuildingSwapPass.Roll($"{key}/{item.InstanceId}")) is not { } answer)
                yield return item;
            else if (answer.Length > 0 && _db.Find(answer) is { } replacement) yield return item with { Building = replacement };
        }
    }

    /// <summary>
    /// The layout's loose-item slots: its objects whose target is an ITEM_PLACEMENT_GROUP (<see cref="LayoutItem.Building"/> is then the group;
    /// docs/game/factions-squads-towns.md "Loose items in buildings"), in instance id order.
    /// </summary>
    public IEnumerable<LayoutItem> ItemGroups(GameRecord layout) => Placed(layout, FcsRecordType.ItemPlacementGroup);

    /// <summary>
    /// The layout's objects that are not cleared and whose target is a <paramref name="type"/> record, in instance id order.
    /// </summary>
    IEnumerable<LayoutItem> Placed(GameRecord layout, FcsRecordType type)
    {
        foreach (var i in layout.Instances.Values.Where(i => !i.IsCleared).OrderBy(i => i.Id, StringComparer.Ordinal))
            if (_db.Find(i.Target) is { } b && b.Type == type)
            {
                var state = i.States.Count > 0 ? _interiors?.Find(i.States[0]) : null;
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

    /// <summary>
    /// A building state's outside flag: <c>flags</c> bit 0, or the older bool <c>is outside</c> without it (docs/formats/zones.md).
    /// </summary>
    public static bool IsOutside(GameRecord state) => state.Ints.TryGetValue("flags", out int flags) ? (flags & 1) != 0 : state.GetBool("is outside");

    /// <summary>
    /// <paramref name="q"/> normalised; identity for a zero quaternion.
    /// </summary>
    static Quaternion Normalized(Quaternion q) => q.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(q);
}