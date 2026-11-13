using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>
/// What placed a <see cref="PlacedMesh"/>.
/// </summary>
public enum PlacedKind
{
    /// <summary>
    /// A BUILDING_PART of a placed building.
    /// </summary>
    BuildingPart,
    /// <summary>
    /// A placed MAP_FEATURES record.
    /// </summary>
    MapFeature,

    /// <summary>
    /// A building's <c>distant mesh</c> (see <see cref="WorldObjectLayout.DistantMesh"/>): the low-poly stand-in for a whole building, not a part.
    /// </summary>
    BuildingDistant,

    /// <summary>
    /// A loose item lying in the world (<see cref="WorldObjectLayout.Item"/>): its record's mesh, one piece.
    /// </summary>
    Item,
}

/// <param name="MeshPath">The mesh file as stored in the record (an install-relative <c>.\data\...</c> path).</param>
/// <param name="Transform">Mesh space to world (System.Numerics row-vector convention: <c>world = local × Transform</c>).</param>
/// <param name="Source">The record naming the mesh: a BUILDING_PART or MAP_FEATURES record.</param>
/// <param name="Owner">The placed record: the BUILDING (a door's own BUILDING for door parts), or the MAP_FEATURES record again.</param>
public sealed record PlacedMesh(
    string MeshPath,
    Matrix4x4 Transform,
    GameRecord Source,
    GameRecord Owner,
    string PlacementId,
    PlacedKind Kind)
{
    /// <summary>
    /// For building parts: the MATERIAL_SPEC the game gives the part (collections already resolved), or null when none.
    /// </summary>
    public GameRecord? Material { get; init; }

    /// <summary>
    /// For building parts: the part's <c>building floor</c>, clamped to 0..3 as the game clamps it; in the interior view a part above the
    /// viewed floor is hidden (docs/formats/zones.md, "Interior view").
    /// </summary>
    public int Floor { get; init; }

    /// <summary>
    /// A part of the building's <c>interior</c> list (inner walls, floors, inner stairs): drawn only in the interior view.
    /// </summary>
    public bool Interior { get; init; }

    /// <summary>
    /// For a door's parts: the door's index in the building's <c>doors</c> list (the save's suffix, the fixture's <c>#i</c>); -1 for every other
    /// part.
    /// </summary>
    public int DoorIndex { get; init; } = -1;

    /// <summary>
    /// For a turret's parts that turn to the target and those hanging from them: the joints above the part (and its own), outermost first; null for
    /// every other part.
    /// </summary>
    public IReadOnlyList<TurnJoint>? Joints { get; init; }

    /// <summary>
    /// For an object of a building layout (sign, furniture): the placement id of the building it belongs to, whose interior view it follows.
    /// </summary>
    public string? ParentPlacementId { get; init; }

    /// <summary>
    /// A building of its own placed inside another (furniture or a station the player built there, a town's piece with <c>is inside building</c>) and
    /// not flagged outside: hidden while the building whose hull holds it shows its outside (game: an interior's contained objects;
    /// docs/render-objects.md "Closed interiors").
    /// </summary>
    public bool Contained { get; init; }

    /// <summary>
    /// For a <see cref="Contained"/> player building: the building it was placed in (its parent, a site's id or a level building's instance id); null
    /// when unknown.
    /// </summary>
    public string? ContainerId { get; init; }

    /// <summary>
    /// A destroyed building's upper-floor part with no destroyed mesh: kept for its collision, never drawn.
    /// </summary>
    public bool RenderHidden { get; init; }

    /// <summary>
    /// A destroyed building's stairs or upper-floor part whose destroyed mesh stands in for a blank own mesh: its collision is the <c>destroyed
    /// collision</c> only, never <c>xml collision</c>.
    /// </summary>
    public bool BlankXmlCollision { get; init; }

    /// <summary>
    /// The collision file the part has in this placement (<see cref="WorldObjectLayout.DestroyedCollision"/>); empty for none.
    /// </summary>
    public string CollisionPath(bool destroyed) => WorldObjectLayout.DestroyedCollision(Source, destroyed, BlankXmlCollision);
}

/// <summary>
/// A joint of a laid-out building: a turret part (a BUILDING_PART with <c>rotation function</c> ROTATION_TARGET) or a part that turns by itself
/// (<paramref name="Rotor"/>, functions 1 to 4). <paramref name="PartId"/> is the part record, <paramref name="Pivot"/> its node's world place and
/// <paramref name="Axis"/> the world direction of the axis it turns about (a turret's node's Y for a yaw part, X for the gun's pitch; a rotor's
/// <c>rotation axis</c>), both of the building as placed (a turret at angle 0, a rotor at its <see cref="RotorSpec.Start"/>). A mesh's pose is its
/// placement times, from its innermost joint out to the outermost, a turn about that joint's pivot and axis by the joint's angle (<c>TurretRig</c>'s
/// angles; a rotor's angle less its start).
/// </summary>
public readonly record struct TurnJoint(string PartId, Vector3 Pivot, Vector3 Axis, bool Yaw, RotorSpec? Rotor = null);

/// <summary>
/// A LIGHT instance of a placed building (docs/formats/lights.md); <see cref="WorldLights"/> turns it into a <see cref="WorldLight"/>.
/// </summary>
/// <param name="Light">The LIGHT record.</param>
/// <param name="Holder">The record listing the instance: a BUILDING_PART, or the BUILDING itself.</param>
/// <param name="Owner">The placed BUILDING (a door's own BUILDING for lights on door parts).</param>
/// <param name="Position">World position.</param>
/// <param name="Direction">World direction a spot light points in (unit length); also set for point lights.</param>
/// <param name="Power">The light's power: <c>brightness</c> plus the building's roll in ±<c>variance</c>/2.</param>
/// <param name="Function">The owner's <c>function</c>, read from its BUILDING_FUNCTIONALITY (-1 without one): the BUILDING itself has no
/// such field.</param>
public sealed record PlacedLight(
    GameRecord Light,
    GameRecord Holder,
    GameRecord Owner,
    string PlacementId,
    string InstanceId,
    Vector3 Position,
    Vector3 Direction,
    float Power,
    int Function = -1);

/// <summary>
/// An EFFECT instance of a placed building (docs/formats/lights.md, "Effects on buildings"): a campfire's fire, a furnace's smoke.
/// </summary>
/// <param name="Effect">The EFFECT record.</param>
/// <param name="Holder">The record listing the instance: a BUILDING_PART, or the BUILDING itself.</param>
/// <param name="Owner">The placed BUILDING (a door's own BUILDING for effects on door parts).</param>
/// <param name="Position">World position: the instance position under the holder's node, not scaled.</param>
/// <param name="Orientation">World turn: the holder node's rotation × the instance rotation.</param>
public sealed record PlacedEffect(
    GameRecord Effect,
    GameRecord Holder,
    GameRecord Owner,
    string PlacementId,
    Vector3 Position,
    Quaternion Orientation);

/// <summary>
/// State of a placed building that changes what is drawn (from its GAMESTATE_BUILDING and its town).
/// </summary>
/// <param name="Destroyed">The state's <c>destroyed</c> flag: parts switch to their <c>destroyed mesh</c>, upper floors and stairs without
/// one go.</param>
/// <param name="TownMaterial">The building's town's material (see <see cref="WorldObjectLayout.TownMaterial"/>), used when the building has none of
/// its own.</param>
public sealed record BuildingState(bool Destroyed = false, GameRecord? TownMaterial = null)
{
    /// <summary>
    /// Also lay out the building's <c>interior</c> list (flagged <see cref="PlacedMesh.Interior"/>), for a renderer that shows the interior view.
    /// </summary>
    public bool WithInterior { get; init; }

    /// <summary>
    /// The <see cref="PlacedMesh.PlacementId"/> and <see cref="PlacedLight.PlacementId"/> to give the output (a <see cref="BuildingKey"/>); null: the
    /// placement id, which also seeds the material choice either way.
    /// </summary>
    public string? Identity { get; init; }
}

/// <summary>
/// Turns world placements into meshes with world transforms (docs/formats/zones.md, "From placements to meshes"):
/// a BUILDING is the BUILDING_PART records of its <c>parts</c> list, chosen by group and chance with the game's
/// random generator seeded from the building's position (<see cref="BuildingRandom"/>), recursively; each part draws
/// its <c>phs or mesh</c> (or <c>destroyed mesh</c>) on its own scene node: moved by <c>offset X/Y/Z</c> × <c>scale</c>,
/// scaled by <c>scale</c>, parented to its parent part's node or the building's (position + rotation). Doors listed in
/// <c>doors</c> are buildings of their own at the same position, rotation and <c>scale</c>. A map feature draws its MAP_FEATURES
/// <c>mesh</c> scaled, rotated and moved by its <c>features.dat</c> entry.
/// </summary>
public static class WorldObjectLayout
{
    /// <summary>
    /// MATERIAL_SPEC used for a building part when neither it, its building nor a town gives one (game: "360-gamedata.base").
    /// </summary>
    public const string FallbackPartMaterial = "360-gamedata.base";

    /// <summary>
    /// MATERIAL_SPEC of a town without its own <c>material</c> (game: "742-gamedata.base").
    /// </summary>
    public const string DefaultTownMaterial = "742-gamedata.base";

    /// <summary>
    /// The BUILDING_PART the character editor's stage stands the characters on (<c>FUN_1405f03c0</c>; base data: "character_editor_platform_part").
    /// </summary>
    public const string EditorPlatform = "5370-platform_otto.mod";

    /// <summary>
    /// Local scale, then rotation, then translation.
    /// </summary>
    public static Matrix4x4 InstanceTransform(Vector3 position, Quaternion rotation, Vector3 scale) =>
        Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(Normalized(rotation)) * Matrix4x4.CreateTranslation(position);

    /// <summary>
    /// <paramref name="q"/> normalised; identity for a zero quaternion.
    /// </summary>
    static Quaternion Normalized(Quaternion q) => q.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(q);

    /// <summary>
    /// A loose item on the ground (docs/simulation.md "Ground items"): its <see cref="ItemMesh"/> at <paramref name="worldPosition"/> turned by
    /// <paramref name="rotation"/>; its look is the material resolver's pick for the record (its <c>material</c> list), or
    /// <paramref name="material"/> (a weapon's model, MATERIAL_SPECS_WEAPON) when given.
    /// </summary>
    public static PlacedMesh? Item(
        GameRecord item,
        string placementId,
        Vector3 worldPosition,
        Quaternion rotation,
        string? mesh = null,
        float scale = 1,
        GameRecord? material = null)
    {
        mesh ??= ItemMesh(item);
        if (mesh.Length == 0) return null;

        var transform = InstanceTransform(worldPosition, rotation, new Vector3(scale));
        return new PlacedMesh(mesh, transform, item, item, placementId, PlacedKind.Item) { Material = material };
    }

    /// <summary>
    /// The character editor's platform (docs/characters.md "Character editor", Preview): <see cref="EditorPlatform"/>'s <c>phs or mesh</c> with its
    /// first <c>material</c> (a resolved collection; none: the mesh's own), at <paramref name="transform"/>, the characters' feet. Null when the
    /// merged data has no such part or it names no mesh.
    /// </summary>
    public static PlacedMesh? EditorPlatformMesh(GameDatabase db, Matrix4x4 transform)
    {
        if (db.Find(EditorPlatform) is not { } part) return null;
        if (part.GetPath("phs or mesh") is not { } mesh || !mesh.Contains(".mesh", StringComparison.Ordinal)) return null;

        var refs = part.GetReferences("material");
        var own = refs.Count > 0 && db.Find(refs[0].TargetStringId) is { Type: FcsRecordType.MaterialSpec } spec ? spec : null;
        var material = own is null ? null : ResolveCollection(db, own, EditorPlatform);
        return new PlacedMesh(mesh, transform, part, part, "meitou:editor-platform", PlacedKind.BuildingPart) { Material = material };
    }

    /// <summary>
    /// Variant <paramref name="n"/> (1-based) of a mesh an item with <c>number of meshes</c> above 1 names (docs/game/factions-squads-towns.md "Nest
    /// furniture"): the file name with its last character replaced by the number (<c>bone1.mesh</c> -> <c>bone7.mesh</c>), no folder; the name itself
    /// when it has no stem.
    /// </summary>
    public static string MeshVariant(string mesh, int n)
    {
        string bare = Path.GetFileNameWithoutExtension(mesh.Replace('\\', '/'));
        return bare.Length == 0 ? mesh : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bare[..^1]}{n}.mesh");
    }

    /// <summary>
    /// The mesh a loose item is drawn with: its <c>ground mesh</c>, else its <c>mesh</c>, else <c>steel1.mesh</c> (<c>FUN_140760300</c>,
    /// <b>Verified</b>). Not modelled: a weapon that exists bare takes its <c>bare sword</c> in place of <c>mesh</c> there (the flag from a save's
    /// <c>bare weapon</c>; for a drop <b>Unknown</b>).
    /// </summary>
    public static string ItemMesh(GameRecord item) =>
        item.GetPath("ground mesh") is { Length: > 0 } ground
            ? ground
            : item.GetPath("mesh") is { Length: > 0 } mesh
                ? mesh
                : "steel1.mesh";

    /// <summary>
    /// Meshes of a placed building and of the doors it lists. <paramref name="worldPosition"/> is the absolute position
    /// (for zone placements: X/Z of the instance and the state's <c>world Y pos</c>, or terrain height + instance Y); its X and
    /// Z seed the part choice, as in the game.
    /// </summary>
    public static List<PlacedMesh> Building(
        GameDatabase db,
        GameRecord building,
        string placementId,
        Vector3 worldPosition,
        Quaternion rotation,
        BuildingState? state = null) => Building(db, building, placementId, worldPosition, rotation, state, lights: null);

    /// <summary>
    /// As <see cref="Building(GameDatabase, GameRecord, string, Vector3, Quaternion, BuildingState?)"/>, and adds the building's LIGHT instances (and
    /// its doors') to <paramref name="lights"/> and its EFFECT instances to <paramref name="effects"/> when given (docs/formats/lights.md,
    /// "Placement", "Effects on buildings"). The meshes are the same either way.
    /// </summary>
    public static List<PlacedMesh> Building(
        GameDatabase db,
        GameRecord building,
        string placementId,
        Vector3 worldPosition,
        Quaternion rotation,
        BuildingState? state,
        List<PlacedLight>? lights,
        List<PlacedEffect>? effects = null)
    {
        state ??= new BuildingState();
        var result = new List<PlacedMesh>();
        AddBuilding(db, building, placementId, worldPosition, rotation, state, result, lights, effects);

        // Doors: one building per "doors" entry at the parent's position, rotation and scale (the door's own scale is not used: Defensive
        // Gate III/IV are 0.85, their doors 1), not for destroyed buildings (game: Building::createPhysical).
        if (!state.Destroyed && !building.GetBool("is node"))
            AddDoors(db, building, placementId, worldPosition, rotation, state, result, lights, effects);

        return result;
    }

    /// <summary>
    /// Adds the building's doors (its <c>doors</c> list) as buildings of their own at the parent's position, rotation and scale.
    /// </summary>
    static void AddDoors(
        GameDatabase db,
        GameRecord building,
        string placementId,
        Vector3 worldPosition,
        Quaternion rotation,
        BuildingState state,
        List<PlacedMesh> result,
        List<PlacedLight>? lights,
        List<PlacedEffect>? effects)
    {
        var doors = building.GetReferences("doors");
        for (int i = 0; i < doors.Count; i++)
        {
            if (db.Find(doors[i].TargetStringId) is { Type: FcsRecordType.Building } door)
                AddBuilding(db, door, placementId, worldPosition, rotation, state, result, lights, effects, i, building.GetFloat("scale", 1));
        }
    }

    /// <summary>
    /// A wind generator's rotor as this placement spawns it (<c>getWindRotationPower</c> reads only the parts the layout created, <b>Verified</b>,
    /// FUN_1404cbd90): the first laid-out part that turns with the wind speed (rotation function 3, no collision file) and its wind range; null when
    /// the position's part choice spawns none.
    /// </summary>
    public static (float Min, float Max)? WindRotor(GameDatabase db, GameRecord building, Vector3 worldPosition)
    {
        foreach (var mesh in Building(db, building, building.StringId, worldPosition, Quaternion.Identity))
        {
            if (mesh.Joints is not { Count: > 0 } joints) continue;
            if (joints[^1] is not { Rotor.Function: RotorSpec.WindSpeed } own || own.PartId != mesh.Source.StringId) continue;

            return (own.Rotor.WindMin, MathF.Max(own.Rotor.WindMin, own.Rotor.WindMax));
        }

        return null;
    }

    /// <summary>
    /// The state of laying out one building: its inputs, its outputs and the values its parts inherit.
    /// </summary>
    sealed class Assembly(
        GameDatabase db,
        GameRecord building,
        string placementId,
        BuildingState state,
        List<PlacedMesh> result,
        List<PlacedLight>? lights,
        List<PlacedEffect>? effects)
    {
        /// <summary>
        /// The database the parts are found in.
        /// </summary>
        public readonly GameDatabase Db = db;
        /// <summary>
        /// The BUILDING being laid out.
        /// </summary>
        public readonly GameRecord Building = building;
        /// <summary>
        /// The placement's id, part of the seed of material collections.
        /// </summary>
        public readonly string PlacementId = placementId;

        /// <summary>
        /// What the output is named by (<see cref="BuildingState.Identity"/>).
        /// </summary>
        public readonly string Identity = state.Identity ?? placementId;
        /// <summary>
        /// The placement's state (town material, interior, identity).
        /// </summary>
        public readonly BuildingState State = state;
        /// <summary>
        /// The meshes laid out.
        /// </summary>
        public readonly List<PlacedMesh> Result = result;
        /// <summary>
        /// The lights laid out; null when not wanted.
        /// </summary>
        public readonly List<PlacedLight>? Lights = lights;
        /// <summary>
        /// The effects laid out; null when not wanted.
        /// </summary>
        public readonly List<PlacedEffect>? Effects = effects;
        /// <summary>
        /// The building's scale (its parent's for a door), never 0 or less.
        /// </summary>
        public float Scale = 1;
        /// <summary>
        /// The building's base material: its first <c>material</c> resolved, else its town's.
        /// </summary>
        public GameRecord? BaseMaterial;
        /// <summary>
        /// The building's random source, seeded by its position (and seeded again for the interior).
        /// </summary>
        public BuildingRandom Random = new(0);
        /// <summary>
        /// The building's scene node: its position and rotation, without the scale.
        /// </summary>
        public Matrix4x4 Node = Matrix4x4.Identity;

        /// <summary>
        /// Laying out the <c>interior</c> list.
        /// </summary>
        public bool Interior;

        /// <summary>
        /// Laying out door i of the parent's <c>doors</c> list; -1 for the building itself.
        /// </summary>
        public int DoorIndex = -1;

        /// <summary>
        /// The last position-marker part's marker (<see cref="PositionMarker"/>).
        /// </summary>
        public Vector3? Marker;
    }

    /// <summary>
    /// The building's position marker (<c>getPositionMarker</c> of a building with fewer than two use nodes, FUN_14054b850; <b>Verified</b>,
    /// FUN_14055f6e0:376-425): a part with a mesh flagged <c>is for position marker</c> sets it to the building's position plus its orientation
    /// applied to the part's offset times the building's scale (the mesh itself is not offset); the last such part wins. Null when the placement
    /// spawns none (the marker is then the building's position).
    /// </summary>
    public static Vector3? PositionMarker(GameDatabase db, GameRecord building, Vector3 worldPosition, Quaternion rotation) =>
        AddBuilding(db, building, building.StringId, worldPosition, rotation, new BuildingState(), [], null, null)?.Marker;

    /// <summary>
    /// Lays out <paramref name="building"/> at <paramref name="position"/>, its interior too when <paramref name="state"/> asks; null for an <c>is
    /// node</c> building.
    /// </summary>
    static Assembly? AddBuilding(
        GameDatabase db,
        GameRecord building,
        string placementId,
        Vector3 position,
        Quaternion rotation,
        BuildingState state,
        List<PlacedMesh> result,
        List<PlacedLight>? lights,
        List<PlacedEffect>? effects,
        int doorIndex = -1,
        float? parentScale = null)
    {
        if (building.GetBool("is node")) return null;

        float scale = parentScale ?? building.GetFloat("scale", 1);
        var a = new Assembly(db, building, placementId, state, result, lights, effects)
        {
            Scale = scale > 0 ? scale : 1,

            // Base material: the building's first "material" (a collection resolved), else its town's (fcs.def: "the local town material").
            BaseMaterial = building.GetReferences("material") is { Count: > 0 } m
                ? ResolveCollection(db, BaseMaterialSpec(db, m[0]), placementId + building.StringId)
                : state.TownMaterial,
            Random = new BuildingRandom(BuildingRandom.Seed(position.X, position.Z)),
            DoorIndex = doorIndex,
        };

        // The building's scene node: position and rotation, no scale (each part node carries the scale).
        a.Node = Matrix4x4.CreateFromQuaternion(Normalized(rotation)) * Matrix4x4.CreateTranslation(position);
        AddParts(a, building, a.Node, 0);

        // The interior parts are chosen from the "interior" list after the generator is seeded again with the same seed (zones.md).
        if (state.WithInterior && building.GetReferences("interior").Count > 0)
        {
            a.Random = new BuildingRandom(BuildingRandom.Seed(position.X, position.Z));
            a.Interior = true;
            AddParts(a, building, a.Node, 0, "interior");
        }

        return a;
    }

    /// <summary>
    /// The MATERIAL_SPEC a building's <c>material</c> reference names, or null when it names none of that type.
    /// </summary>
    static GameRecord? BaseMaterialSpec(GameDatabase db, GameReference reference) =>
        db.Find(reference.TargetStringId) is { Type: FcsRecordType.MaterialSpec } spec ? spec : null;

    /// <summary>
    /// One level of the game's part creation: choose from <paramref name="holder"/>'s parts, create each, recurse, then the holder's instances.
    /// </summary>
    static void AddParts(
        Assembly a,
        GameRecord holder,
        Matrix4x4 parentNode,
        int depth,
        string list = "parts",
        IReadOnlyList<TurnJoint>? joints = null)
    {
        if (depth > 8) return;

        foreach (var r in ChooseParts(holder.GetReferences(list), id => a.Db.Find(id) is not null, a.Random))
        {
            if (a.Db.Find(r.TargetStringId) is not { Type: FcsRecordType.BuildingPart } part) continue;

            // Children hang under the part's node, or under the building's when the part has no entity (they then keep the joints above the part)
            // or is an interior part (FUN_1405609e0 recurses into those with no parent, so a scaled building's interior floor is scaled once).
            var node = AddPart(a, holder, part, parentNode, joints, out var below);
            var childNode = list == "interior" ? a.Node : node ?? a.Node;
            AddParts(a, part, childNode, depth + 1, "parts", below);
        }

        // The building's own instances once, with its parts.
        if (list == "parts") AddInstances(a, holder, parentNode);
    }

    /// <summary>
    /// A part's scene node and mesh. Returns the node children attach to, or null when the part has no entity (they then attach
    /// to the building's node).
    /// </summary>
    static Matrix4x4? AddPart(
        Assembly a,
        GameRecord holder,
        GameRecord part,
        Matrix4x4 parentNode,
        IReadOnlyList<TurnJoint>? joints,
        out IReadOnlyList<TurnJoint>? below)
    {
        below = joints;
        var mesh = part.GetPath("phs or mesh");
        bool entity = true, blankCollision = false;
        if (a.State.Destroyed && !DestroyedPart(part, ref mesh, out entity, out blankCollision)) return null;
        if (mesh.Length == 0 || !mesh.Contains(".mesh", StringComparison.Ordinal)) return null;

        var offset = part.GetBool("is for position marker")
            ? Vector3.Zero
            : new Vector3(part.GetFloat("offset X"), part.GetFloat("offset Y"), part.GetFloat("offset Z")) * a.Scale;
        var local = Matrix4x4.CreateScale(a.Scale);
        if (part.GetBool("is for position marker"))
        {
            var marker = new Vector3(part.GetFloat("offset X"), part.GetFloat("offset Y"), part.GetFloat("offset Z")) * a.Scale;
            a.Marker = Vector3.Transform(marker, a.Node);
        }

        string collision = DestroyedCollision(part, a.State.Destroyed, blankCollision);
        var rotor = RollRotor(a, holder, part, parentNode, entity, collision, ref local, out var rotorAxis);

        local *= Matrix4x4.CreateTranslation(offset);
        var node = local * parentNode;
        if (!entity)
        {
            AddCollisionOnlyPart(a, part, mesh, node);
            return null;
        }

        below = PartJoints(part, node, joints, rotor, rotorAxis);
        a.Result.Add(new PlacedMesh(mesh, node, part, a.Building, a.Identity, PlacedKind.BuildingPart)
        {
            Material = PartMaterial(a, part),
            Floor = Math.Clamp(part.GetInt("building floor"), 0, 3),
            Interior = a.Interior,
            DoorIndex = a.DoorIndex,
            Joints = below,
            BlankXmlCollision = blankCollision,
        });
        return node;
    }

    /// <summary>
    /// Rotating parts (no collision file, a rotation function on the part or its holder; RotatingEnt's constructor) roll a speed variation
    /// (unless ROTATION_WIND_SPEED) and a start angle about their axis, turned into <paramref name="local"/>. Returns the rotor of a part that
    /// turns by itself, with its axis in <paramref name="rotorAxis"/>; null otherwise. The viewer leaves turret parts (ROTATION_TARGET) unrotated.
    /// </summary>
    static RotorSpec? RollRotor(
        Assembly a,
        GameRecord holder,
        GameRecord part,
        Matrix4x4 parentNode,
        bool entity,
        string collision,
        ref Matrix4x4 local,
        out Vector3 rotorAxis)
    {
        RotorSpec? rotor = null;
        rotorAxis = Vector3.Zero;
        bool rotates = collision.Length == 0 && (part.GetInt("rotation function") > 0 || holder.GetInt("rotation function") > 0);
        if (!rotates) return rotor;

        int function = part.GetInt("rotation function");
        if (function == 0) function = 6;

        float speedMin = float.DegreesToRadians(part.GetFloat("rotation speed min"));
        float speedMax = float.DegreesToRadians(part.GetFloat("rotation speed max"));
        float windMin = part.GetFloat("wind speed rotation min");
        float windMax = part.GetFloat("wind speed rotation max");
        if (function == RotorSpec.WindSpeed) (speedMax, windMax) = (MathF.Max(speedMin, speedMax), MathF.Max(windMin, windMax));
        else speedMax = a.Random.NextFloat(speedMax * 0.95f, speedMax * 1.05f);
        if (!entity) return rotor;

        float angle = a.Random.NextFloat(0, MathF.Tau);
        rotorAxis = part.GetInt("rotation axis", 2) switch
        {
            0 => Vector3.UnitX,
            1 => Vector3.UnitY,
            2 => Vector3.UnitZ,
            _ => Vector3.Zero,
        };
        if (function != 5 && rotorAxis != Vector3.Zero) local *= Matrix4x4.CreateFromAxisAngle(rotorAxis, angle);
        if (function is >= RotorSpec.Constant and <= RotorSpec.WindDirection && rotorAxis != Vector3.Zero)
        {
            var spinAxis = Vector3.Normalize(new Vector3(parentNode.M11, parentNode.M12, parentNode.M13));
            var windAxis = Vector3.Normalize(new Vector3(parentNode.M31, parentNode.M32, parentNode.M33));
            rotor = new RotorSpec(function, speedMin, speedMax, windMin, windMax, angle, spinAxis, windAxis);
        }
        return rotor;
    }

    /// <summary>
    /// A part of an upper floor with no destroyed mesh keeps its mesh as collision only (FUN_14055f6e0 mode 2: render skipped, collision made);
    /// children hang from the building.
    /// </summary>
    static void AddCollisionOnlyPart(Assembly a, GameRecord part, string mesh, Matrix4x4 node)
    {
        a.Result.Add(new PlacedMesh(mesh, node, part, a.Building, a.Identity, PlacedKind.BuildingPart)
        {
            Material = PartMaterial(a, part),
            Floor = Math.Clamp(part.GetInt("building floor"), 0, 3),
            Interior = a.Interior,
            DoorIndex = a.DoorIndex,
            RenderHidden = true,
        });
    }

    /// <summary>
    /// The joints above the part's children: <paramref name="joints"/>, and the part itself when it turns (by itself, or to the target).
    /// </summary>
    static IReadOnlyList<TurnJoint>? PartJoints(GameRecord part, Matrix4x4 node, IReadOnlyList<TurnJoint>? joints, RotorSpec? rotor, Vector3 rotorAxis)
    {
        var below = joints;

        // A part that turns by itself is a joint too: it and everything hanging from it turn about its node's axis.
        if (rotor is not null)
        {
            var axis = Vector3.TransformNormal(rotorAxis, node);
            if (axis.LengthSquared() > 1e-12f)
            {
                var turnJoint = new TurnJoint(part.StringId, node.Translation, Vector3.Normalize(axis), rotorAxis == Vector3.UnitY, rotor);
                below = [.. joints ?? [], turnJoint];
            }
        }

        // A part that turns to the target (ROTATION_TARGET) is a joint: it and everything hanging from it turn about its node (see TurnJoint).
        if (part.GetInt("rotation function") == 5)
        {
            bool yaw = part.GetInt("rotation axis", 2) == 1;
            var axis = yaw ? new Vector3(node.M21, node.M22, node.M23) : new Vector3(node.M11, node.M12, node.M13);
            if (axis.LengthSquared() > 1e-12f)
            {
                var turnJoint = new TurnJoint(part.StringId, node.Translation, Vector3.Normalize(axis), yaw);
                below = [.. joints ?? [], turnJoint];
            }
        }
        return below;
    }

    /// <summary>
    /// A destroyed building's part (FUN_14055f6e0:144-176, <b>Verified</b>; fcs.def: "Ground floor will use normal mesh if blank. Upper floors will
    /// be removed if blank."). Returns false when the part is dropped (stairs without a destroyed mesh). <paramref name="mesh"/> becomes the
    /// destroyed mesh when it has one. <paramref name="entity"/> is false for a non-stairs part above the ground floor with no destroyed mesh: its
    /// normal mesh stays for collision only, not drawn. <paramref name="blankXmlCollision"/>: a stairs or upper-floor part whose destroyed mesh
    /// replaces a blank own mesh loses its <c>xml collision</c> (the original blanks it in the shared record, so the intact building's part loses it
    /// too once any destroyed one was made; not modelled).
    /// </summary>
    static bool DestroyedPart(GameRecord part, ref string mesh, out bool entity, out bool blankXmlCollision)
    {
        entity = true;
        blankXmlCollision = false;
        var destroyed = part.GetPath("destroyed mesh");
        bool stairs = part.GetBool("is stairs");
        if (stairs && destroyed.Length == 0) return false;

        bool upper = part.GetInt("building floor") != 0;
        if (!stairs && !upper)
        {
            if (destroyed.Length > 0) mesh = destroyed;
            return true;
        }

        if (destroyed.Length == 0)
        {
            entity = false;
        }
        else
        {
            blankXmlCollision = mesh.Length == 0;
            mesh = destroyed;
        }

        return true;
    }

    /// <summary>
    /// A part's collision file: the destroyed building's <c>destroyed collision</c> when it has one, else <c>xml collision</c> (blank when
    /// <paramref name="blankXml"/>).
    /// </summary>
    public static string DestroyedCollision(GameRecord part, bool destroyed, bool blankXml)
    {
        if (destroyed && part.GetPath("destroyed collision") is { Length: > 0 } dc) return dc;
        if (blankXml) return "";

        return part.GetPath("xml collision");
    }

    /// <summary>
    /// A record's instances after its parts, with the rolls the game draws for them: two per LIGHT (its brightness variance, then a second draw of
    /// the same range kept by the light's owner; docs/formats/lights.md), one per node BUILDING (its handle), none per EFFECT. LIGHT and EFFECT
    /// instances are added to <see cref="Assembly.Lights"/> and <see cref="Assembly.Effects"/> when set; <paramref name="holderNode"/> is the
    /// holder's scene node (its part's node, or the building's when the holder is the building or made no entity).
    /// </summary>
    static void AddInstances(Assembly a, GameRecord holder, Matrix4x4 holderNode)
    {
        // The game walks the instances in id order (a std::map).
        foreach (var instance in holder.Instances.Values.Where(i => !i.IsCleared).OrderBy(i => i.Id, StringComparer.Ordinal))
        {
            if (a.Db.Find(instance.Target) is not { } target) continue;

            if (target.Type == FcsRecordType.Light)
            {
                float halfVariance = target.GetFloat("variance") * 0.5f;
                float power = target.GetFloat("brightness", 1) + a.Random.NextFloat(-halfVariance, halfVariance);
                a.Random.Next();
                a.Lights?.Add(PlaceLight(a, holder, holderNode, instance, target, power));
            }
            else if (target.Type == FcsRecordType.Effect && a.Effects is { } effects)
            {
                // Unlike a light's, the effect node's offset is not multiplied by the building scale.
                Matrix4x4.Decompose(holderNode, out _, out var turn, out _);
                var position = Vector3.Transform(instance.Position, holderNode);
                var orientation = Normalized(Normalized(turn) * Normalized(instance.Rotation));
                effects.Add(new PlacedEffect(target, holder, a.Building, a.Identity, position, orientation));
            }
            else if (target.Type == FcsRecordType.Building && target.GetBool("is node"))
            {
                a.Random.Next();
            }
        }
    }

    /// <summary>
    /// A LIGHT instance in world space: its node hangs under the holder's node at the instance position × the building <c>scale</c>,
    /// and a spot light points along the instance rotation applied to −Y (docs/formats/lights.md, "Spot direction").
    /// </summary>
    static PlacedLight PlaceLight(Assembly a, GameRecord holder, Matrix4x4 holderNode, GameInstance instance, GameRecord light, float power)
    {
        var position = Vector3.Transform(instance.Position * a.Scale, holderNode);
        var local = Vector3.Transform(-Vector3.UnitY, Normalized(instance.Rotation));
        var direction = Vector3.TransformNormal(local, holderNode);
        direction = direction.LengthSquared() > 1e-12f ? Vector3.Normalize(direction) : -Vector3.UnitY;
        int function = FunctionOf(a);
        return new PlacedLight(light, holder, a.Building, a.Identity, instance.Id, position, direction, power, function);
    }

    /// <summary>
    /// The <c>function</c> of the building's first BUILDING_FUNCTIONALITY link, or -1 when it has none.
    /// </summary>
    static int FunctionOf(Assembly a)
    {
        var links = a.Building.GetReferences("functionality");
        if (links is not [var link, ..]) return -1;
        if (a.Db.Find(link.TargetStringId) is not { } fn) return -1;

        return fn.GetInt("function", -1);
    }

    /// <summary>
    /// A part's material (game: Building's part material lookup): its first <c>material</c>, else the building's base, else
    /// <see cref="FallbackPartMaterial"/>.
    /// </summary>
    static GameRecord? PartMaterial(Assembly a, GameRecord part)
    {
        var refs = part.GetReferences("material");
        if (refs.Count > 0 && a.Db.Find(refs[0].TargetStringId) is { Type: FcsRecordType.MaterialSpec } own)
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
        if (spec is not { Type: FcsRecordType.MaterialSpec }) spec = db.Find(DefaultTownMaterial);
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
            .Where(e => e.Record is not null && e.Weight > 0)
            .ToList();
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
    /// The <c>parts</c> entries the game uses, drawing from <paramref name="random"/> exactly as it does (<c>FUN_140848950</c>, val0 group, val1
    /// chance). A group 0 entry is kept without a draw at chance 0 or 100, else when an int roll 0..100 is at most its chance (a missing target rolls
    /// too). Every other group, in ascending group number, draws one float in [0, total) (total: the chance of every entry, 0 counting as 100,
    /// missing targets too), then walks its entries in list order: one whose chance (0 as 100) is below the roll takes it off and goes on, else it is
    /// taken (a missing target yields no part) and the walk stops; a roll past the sum takes nothing. Group 0 picks come first, in list order.
    /// </summary>
    public static List<GameReference> ChooseParts(IReadOnlyList<GameReference> parts, Func<string, bool> exists, BuildingRandom random)
    {
        var result = new List<GameReference>();
        var groups = new SortedDictionary<int, List<GameReference>>();
        foreach (var p in parts)
        {
            int group = p.Values.Value0;
            int chance = p.Values.Value1;
            if (group != 0)
            {
                if (!groups.TryGetValue(group, out var list)) groups[group] = list = [];
                list.Add(p);
            }
            else if (chance == 0 || chance == 100 || random.NextInt(0, 100) <= chance)
            {
                result.Add(p);
            }
        }

        foreach (var list in groups.Values)
        {
            float roll = random.NextFloat(0, list.Sum(e => GroupChance(e)));
            foreach (var entry in list)
            {
                if (GroupChance(entry) < roll)
                {
                    roll -= GroupChance(entry);
                    continue;
                }

                if (exists(entry.TargetStringId)) result.Add(entry);
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// An entry's chance in a non-zero group: 0 stands for 100.
    /// </summary>
    static int GroupChance(GameReference entry) => entry.Values.Value1 == 0 ? 100 : entry.Values.Value1;

    /// <summary>
    /// A building's <c>distant mesh</c> at the building's position, rotation and <c>scale</c> (the transform the game's
    /// town batching gives it, docs/formats/zones.md, "Distant towns"), or null when the building has none.
    /// </summary>
    public static PlacedMesh? DistantMesh(GameRecord building, string placementId, Vector3 worldPosition, Quaternion rotation)
    {
        var mesh = building.GetPath("distant mesh");
        if (!mesh.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase) || building.GetBool("is node")) return null;

        float scale = building.GetFloat("scale", 1);
        var transform = InstanceTransform(worldPosition, rotation, new Vector3(scale > 0 ? scale : 1));
        return new PlacedMesh(mesh, transform, building, building, placementId, PlacedKind.BuildingDistant);
    }

    /// <summary>
    /// The mesh of a <c>features.dat</c> entry, or null when its record is missing, hidden or meshless.
    /// </summary>
    public static PlacedMesh? Feature(GameDatabase db, MapFeature feature, string placementId)
    {
        if (db.Find(feature.StringId) is not { Type: FcsRecordType.MapFeatures } record) return null;
        if (record.GetBool("hidden")) return null;

        var mesh = record.GetPath("mesh");
        if (!mesh.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase)) return null;

        var transform = InstanceTransform(feature.Position, feature.Rotation, feature.Scale);
        return new PlacedMesh(mesh, transform, record, record, placementId, PlacedKind.MapFeature);
    }

    /// <summary>
    /// A stable 32-bit hash (FNV-1a) of a string, for the viewer's own seeded choices.
    /// </summary>
    public static uint Seed(string id)
    {
        uint h = 2166136261;
        foreach (char c in id) h = (h ^ c) * 16777619;
        return h;
    }
}
