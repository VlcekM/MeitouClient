using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>What placed a <see cref="PlacedMesh"/>.</summary>
public enum PlacedKind { BuildingPart, MapFeature }

/// <param name="MeshPath">The mesh file as stored in the record (an install-relative <c>.\data\...</c> path).</param>
/// <param name="Transform">Mesh space to world (System.Numerics row-vector convention: <c>world = local × Transform</c>).</param>
/// <param name="Source">The record naming the mesh: a BUILDING_PART or MAP_FEATURES record.</param>
/// <param name="Owner">The placed record: the BUILDING (a door's own BUILDING for door parts), or the MAP_FEATURES record again.</param>
public sealed record PlacedMesh(string MeshPath, Matrix4x4 Transform, GameRecord Source, GameRecord Owner, string PlacementId, PlacedKind Kind)
{
    /// <summary>For building parts: the MATERIAL_SPEC the game gives the part (collections already resolved), or null when none.</summary>
    public GameRecord? Material { get; init; }
}

/// <summary>State of a placed building that changes what is drawn (from its GAMESTATE_BUILDING and its town).</summary>
/// <param name="Destroyed">The state's <c>destroyed</c> flag: parts switch to their <c>destroyed mesh</c>, upper floors and stairs without one go.</param>
/// <param name="TownMaterial">The building's town's material (see <see cref="WorldObjectLayout.TownMaterial"/>), used when the building has none of its own.</param>
public sealed record BuildingState(bool Destroyed = false, GameRecord? TownMaterial = null);

/// <summary>
/// Turns world placements into meshes with world transforms (docs/formats/zones.md, "From placements to meshes"):
/// a BUILDING is the BUILDING_PART records of its <c>parts</c> list, chosen by group and chance with the game's
/// random generator seeded from the building's position (<see cref="BuildingRandom"/>), recursively; each part draws
/// its <c>phs or mesh</c> (or <c>destroyed mesh</c>) on its own scene node: moved by <c>offset X/Y/Z</c> × <c>scale</c>,
/// scaled by <c>scale</c>, parented to its parent part's node or the building's (position + rotation). Doors listed in
/// <c>doors</c> are buildings of their own at the same position and rotation. A map feature draws its MAP_FEATURES
/// <c>mesh</c> scaled, rotated and moved by its <c>features.dat</c> entry.
/// </summary>
public static class WorldObjectLayout
{
    /// <summary>MATERIAL_SPEC used for a building part when neither it, its building nor a town gives one (game: "360-gamedata.base").</summary>
    public const string FallbackPartMaterial = "360-gamedata.base";

    /// <summary>MATERIAL_SPEC of a town without its own <c>material</c> (game: "742-gamedata.base").</summary>
    public const string DefaultTownMaterial = "742-gamedata.base";

    /// <summary>Local scale, then rotation, then translation.</summary>
    public static Matrix4x4 InstanceTransform(Vector3 position, Quaternion rotation, Vector3 scale) =>
        Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(Normalized(rotation)) * Matrix4x4.CreateTranslation(position);

    static Quaternion Normalized(Quaternion q) => q.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(q);

    /// <summary>
    /// Meshes of a placed building and of the doors it lists. <paramref name="worldPosition"/> is the absolute position
    /// (for zone placements: X/Z of the instance and the state's <c>world Y pos</c>, or terrain height + instance Y); its X and
    /// Z seed the part choice, as in the game.
    /// </summary>
    public static List<PlacedMesh> Building(GameDatabase db, GameRecord building, string placementId, Vector3 worldPosition, Quaternion rotation,
        BuildingState? state = null)
    {
        state ??= new BuildingState();
        var result = new List<PlacedMesh>();
        AddBuilding(db, building, placementId, worldPosition, rotation, state, result, door: false);
        // Doors: one building per "doors" entry at the parent's position and rotation, not for destroyed buildings (game: Building::createPhysical).
        if (!state.Destroyed && !building.GetBool("is node"))
            foreach (var r in building.GetReferences("doors"))
                if (db.Find(r.TargetStringId) is { Type: FcsRecordType.BUILDING } door)
                    AddBuilding(db, door, placementId, worldPosition, rotation, state, result, door: true);
        return result;
    }

    sealed class Assembly(GameDatabase db, GameRecord building, string placementId, BuildingState state, List<PlacedMesh> result)
    {
        public readonly GameDatabase Db = db;
        public readonly GameRecord Building = building;
        public readonly string PlacementId = placementId;
        public readonly BuildingState State = state;
        public readonly List<PlacedMesh> Result = result;
        public float Scale = 1;
        public GameRecord? BaseMaterial;
        public BuildingRandom Random = new(0);
        public Matrix4x4 Node = Matrix4x4.Identity;
    }

    static void AddBuilding(GameDatabase db, GameRecord building, string placementId, Vector3 position, Quaternion rotation, BuildingState state,
        List<PlacedMesh> result, bool door)
    {
        if (building.GetBool("is node")) return;
        float scale = building.GetFloat("scale", 1);
        var a = new Assembly(db, building, placementId, state, result)
        {
            Scale = scale > 0 ? scale : 1,
            // Base material: the building's first "material" (a collection resolved), else its town's (fcs.def: "the local town material").
            BaseMaterial = building.GetReferences("material") is { Count: > 0 } m
                ? ResolveCollection(db, db.Find(m[0].TargetStringId) is { Type: FcsRecordType.MATERIAL_SPEC } spec ? spec : null, placementId + building.StringId)
                : state.TownMaterial,
            Random = new BuildingRandom(BuildingRandom.Seed(position.X, position.Z)),
        };
        // The building's scene node: position and rotation, no scale (each part node carries the scale).
        a.Node = Matrix4x4.CreateFromQuaternion(Normalized(rotation)) * Matrix4x4.CreateTranslation(position);
        AddParts(a, building, a.Node, 0);
    }

    /// <summary>One level of the game's part creation: choose from <paramref name="holder"/>'s parts, create each, recurse, then the holder's instances.</summary>
    static void AddParts(Assembly a, GameRecord holder, Matrix4x4 parentNode, int depth)
    {
        if (depth > 8) return;
        foreach (var r in ChooseParts(holder.GetReferences("parts"), id => a.Db.Find(id) is not null, a.Random))
        {
            if (a.Db.Find(r.TargetStringId) is not { Type: FcsRecordType.BUILDING_PART } part) continue;
            // Children hang under the part's node, or under the building's when the part has no entity.
            var node = AddPart(a, holder, part, parentNode);
            AddParts(a, part, node ?? a.Node, depth + 1);
        }
        ConsumeInstanceRolls(a, holder);
    }

    /// <summary>
    /// A part's scene node and mesh. Returns the node children attach to, or null when the part has no entity (they then attach
    /// to the building's node).
    /// </summary>
    static Matrix4x4? AddPart(Assembly a, GameRecord holder, GameRecord part, Matrix4x4 parentNode)
    {
        var mesh = part.GetPath("phs or mesh");
        bool entity = true;
        if (a.State.Destroyed)
        {
            // fcs.def: "Ground floor will use normal mesh if blank. Upper floors will be removed if blank."; stairs go too.
            var destroyed = part.GetPath("destroyed mesh");
            if (destroyed.Length > 0) mesh = destroyed;
            else if (part.GetBool("is stairs")) return null;
            else if (part.GetInt("building floor") != 0) entity = false;
        }
        if (mesh.Length == 0 || !mesh.Contains(".mesh", StringComparison.Ordinal)) return null;

        var offset = part.GetBool("is for position marker")
            ? Vector3.Zero
            : new Vector3(part.GetFloat("offset X"), part.GetFloat("offset Y"), part.GetFloat("offset Z")) * a.Scale;
        var local = Matrix4x4.CreateScale(a.Scale);

        // Rotating parts (no collision file, a rotation function on the part or its holder) roll a speed variation (unless
        // ROTATION_WIND_SPEED) and a start angle about their axis. The viewer leaves turret parts (ROTATION_TARGET) unrotated.
        string collision = a.State.Destroyed && part.GetPath("destroyed collision") is { Length: > 0 } dc ? dc : part.GetPath("xml collision");
        if (collision.Length == 0 && (part.GetInt("rotation function") > 0 || holder.GetInt("rotation function") > 0))
        {
            int function = part.GetInt("rotation function");
            if (function == 0) function = 6;
            if (function != 3) a.Random.Next();
            if (entity)
            {
                float angle = a.Random.NextFloat(0, MathF.Tau);
                var axis = part.GetInt("rotation axis", 2) switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, 2 => Vector3.UnitZ, _ => Vector3.Zero };
                if (function != 5 && axis != Vector3.Zero) local *= Matrix4x4.CreateFromAxisAngle(axis, angle);
            }
        }
        local *= Matrix4x4.CreateTranslation(offset);
        var node = local * parentNode;
        if (!entity) return null;
        a.Result.Add(new PlacedMesh(mesh, node, part, a.Building, a.PlacementId, PlacedKind.BuildingPart) { Material = PartMaterial(a, part) });
        return node;
    }

    /// <summary>Rolls the game draws for a record's instances after its parts: one per LIGHT, one per node BUILDING (its handle); effects: Unknown, none.</summary>
    static void ConsumeInstanceRolls(Assembly a, GameRecord holder)
    {
        // The game walks the instances in id order (a std::map).
        foreach (var instance in holder.Instances.Values.Where(i => !i.IsCleared).OrderBy(i => i.Id, StringComparer.Ordinal))
            if (a.Db.Find(instance.Target) is { } target &&
                (target.Type == FcsRecordType.LIGHT || target.Type == FcsRecordType.BUILDING && target.GetBool("is node")))
                a.Random.Next();
    }

    /// <summary>A part's material (game: Building's part material lookup): its first <c>material</c>, else the building's base, else <see cref="FallbackPartMaterial"/>.</summary>
    static GameRecord? PartMaterial(Assembly a, GameRecord part)
    {
        var refs = part.GetReferences("material");
        if (refs.Count > 0 && a.Db.Find(refs[0].TargetStringId) is { Type: FcsRecordType.MATERIAL_SPEC } own)
            return ResolveCollection(a.Db, own, a.PlacementId + part.StringId);
        if (a.BaseMaterial is not null) return a.BaseMaterial;
        return ResolveCollection(a.Db, a.Db.Find(FallbackPartMaterial), a.PlacementId + part.StringId);
    }

    /// <summary>
    /// A town's material: the TOWN's first <c>material</c>, else <see cref="DefaultTownMaterial"/>, as a resolved collection.
    /// The game picks from collections with an unseeded roll; here the pick is seeded by <paramref name="seedKey"/> (e.g. the town's instance id).
    /// </summary>
    public static GameRecord? TownMaterial(GameDatabase db, GameRecord town, string seedKey)
    {
        var refs = town.GetReferences("material");
        var spec = refs.Count > 0 ? db.Find(refs[0].TargetStringId) : null;
        if (spec is not { Type: FcsRecordType.MATERIAL_SPEC }) spec = db.Find(DefaultTownMaterial);
        return ResolveCollection(db, spec, seedKey);
    }

    /// <summary>
    /// A MATERIAL_SPEC whose own <c>material</c> list is non-empty is a collection: one entry is used, weighted by val0 (0 counts as
    /// 100). The game rolls without a fixed seed; the viewer's roll is seeded by <paramref name="seedKey"/> so it is stable.
    /// </summary>
    public static GameRecord? ResolveCollection(GameDatabase db, GameRecord? spec, string seedKey)
    {
        if (spec is null) return null;
        var entries = spec.GetReferences("material")
            .Select(r => (Record: db.Find(r.TargetStringId), Weight: r.Values.Value0 == 0 ? 100 : r.Values.Value0))
            .Where(e => e.Record is not null && e.Weight > 0).ToList();
        if (spec.GetReferences("material").Count == 0) return spec;
        if (entries.Count == 0) return null;
        if (entries.Count == 1) return entries[0].Record;
        float total = entries.Sum(e => (float)e.Weight);
        float roll = new BuildingRandom(Seed(seedKey)).NextFloat(0, total);
        float sum = 0;
        foreach (var (record, weight) in entries)
        {
            sum += weight;
            if (roll < sum) return record;
        }
        return entries[^1].Record;
    }

    /// <summary>
    /// The <c>parts</c> entries the game uses, drawing from <paramref name="random"/> exactly as it does (val0 group, val1 chance):
    /// a group 0 entry with chance 0 is always kept; any other group 0 entry rolls an int 0..99 (even at chance ≥ 100 and for
    /// missing targets) and is kept if the roll is below its chance. Other groups collect their entries with chance &gt; 0 and an
    /// existing target; afterwards each group, in the iteration order of the game's hash map, keeps its only entry or rolls a
    /// float in [0, total) and keeps the entry whose running total first exceeds it. Group 0 picks come first, in list order.
    /// </summary>
    public static List<GameReference> ChooseParts(IReadOnlyList<GameReference> parts, Func<string, bool> exists, BuildingRandom random)
    {
        var result = new List<GameReference>();
        var groups = new Dictionary<int, List<(GameReference Ref, float Total)>>();
        var order = new List<int>();
        foreach (var p in parts)
        {
            int group = p.Values.Value0, chance = p.Values.Value1;
            if (group == 0)
            {
                if (chance == 0 || random.NextInt(0, 99) < chance) result.Add(p);
                continue;
            }
            if (!groups.TryGetValue(group, out var list))
            {
                groups[group] = list = [];
                InsertInMapOrder(order, group);
            }
            if (chance > 0 && exists(p.TargetStringId))
                list.Add((p, (list.Count > 0 ? list[^1].Total : 0) + chance));
        }
        foreach (int group in order)
        {
            var list = groups[group];
            if (list.Count == 0) continue; // the game appends an empty entry here
            if (list.Count == 1) { result.Add(list[0].Ref); continue; }
            float roll = random.NextFloat(0, list[^1].Total);
            result.Add(list.FirstOrDefault(e => e.Total > roll, list[^1]).Ref);
        }
        return result;
    }

    /// <summary>Group order of the game's <c>boost::unordered_map&lt;int, ...&gt;</c> (17 buckets, key % 17): a key in a new bucket goes to the front, else before its bucket's first key.</summary>
    static void InsertInMapOrder(List<int> order, int key)
    {
        const ulong buckets = 17; // boost's next prime from the default 11
        ulong bucket = (ulong)(long)key % buckets;
        int at = order.FindIndex(k => (ulong)(long)k % buckets == bucket);
        order.Insert(at < 0 ? 0 : at, key);
    }

    /// <summary>The mesh of a <c>features.dat</c> entry, or null when its record is missing, hidden or meshless.</summary>
    public static PlacedMesh? Feature(GameDatabase db, MapFeature feature, string placementId)
    {
        if (db.Find(feature.StringId) is not { Type: FcsRecordType.MAP_FEATURES } record) return null;
        if (record.GetBool("hidden")) return null;
        var mesh = record.GetPath("mesh");
        if (!mesh.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase)) return null;
        return new PlacedMesh(mesh, InstanceTransform(feature.Position, feature.Rotation, feature.Scale), record, record, placementId, PlacedKind.MapFeature);
    }

    /// <summary>A stable 32-bit hash (FNV-1a) of a string, for the viewer's own seeded choices.</summary>
    public static uint Seed(string id)
    {
        uint h = 2166136261;
        foreach (char c in id) h = (h ^ c) * 16777619;
        return h;
    }
}
