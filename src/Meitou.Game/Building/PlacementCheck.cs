using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Navigation;

namespace Meitou.Game.Building;

/// <summary>
/// A ghost waiting for Confirm: what it is and where it stands, with its footprint in the world.
/// </summary>
public sealed record QueuedGhost(
    BuildingBuildData Building, Vector3 Position, Quaternion Rotation, int Floor, Vector2[]? Hull, string? Parent = null, bool Outside = false);

/// <summary>
/// A ghost standing on a building's floor: the building (its placement id, a player site's own id), its indoor hull in the world, and the floor
/// (0..3). A level building's <paramref name="Level"/> is its instance id (what the placed building names as its parent) and <paramref name="Owned"/>
/// whether the player's faction owns it.
/// </summary>
public sealed record GhostFloor(string Building, Vector2[] Footprint, int Floor, string? Level = null, bool Owned = false)
{
    /// <summary>
    /// A level building that is a ruin now.
    /// </summary>
    public bool Destroyed { get; init; }

    /// <summary>
    /// A level building whose owner faction is <c>not real</c>, or that has no owner.
    /// </summary>
    public bool OwnerNotReal { get; init; }

    /// <summary>
    /// The height of the building's ceiling over the floor point, null when nothing is over it (what a <c>ceiling placement</c> ghost hangs from).
    /// </summary>
    public float? Ceiling { get; init; }

    /// <summary>
    /// The floor surface's upward normal under the ghost's origin (the original's one indoor slope ray); null when it is not known.
    /// </summary>
    public Vector3? Normal { get; init; }

    /// <summary>
    /// Whether the building's own collision (walls, floors, roofs) cuts into a prism by more than a tolerance
    /// (<see cref="InteriorVolume.StructureCuts"/>); null when it is not known.
    /// </summary>
    public Func<FootprintPrism, float, bool>? Structure { get; init; }

    /// <summary>
    /// Whether the building's drawn roofs and ceilings cut into a prism by more than a tolerance (<see cref="InteriorVolume.RoofCuts"/>); null when
    /// it is not known.
    /// </summary>
    public Func<FootprintPrism, float, bool>? Roof { get; init; }

    /// <summary>
    /// The parent the placed building records: the site's id, or the level building's instance id.
    /// </summary>
    public string Parent => Level ?? Building;
}

/// <summary>
/// What the geometric checks read from the world: the terrain, the water, the footprints (<see cref="BuildingFootprints"/>) and the parts of a
/// building.
/// </summary>
public sealed class PlacementServices
{
    /// <summary>
    /// The ground height at (x, z).
    /// </summary>
    public required Func<float, float, float> Ground { get; init; }
    /// <summary>
    /// Water level (Observed 98, docs/game/buildings-production.md "Placement").
    /// </summary>
    public float WaterLevel { get; init; } = 98;
    /// <summary>
    /// A BUILDING's collision footprint; null while it is read, or when it has none.
    /// </summary>
    public required Func<string, BuildingFootprint?> Footprint { get; init; }
    /// <summary>
    /// The level data's buildings round a point (X, Z) within a radius, with their footprints in the world (those read so far).
    /// </summary>
    public required Func<Vector2, float, IEnumerable<PlacedFootprint>> StaticAround { get; init; }
    /// <summary>
    /// The level data's building placements within a radius of a point (X, Z), no footprint needed (the agent control channel's building list).
    /// </summary>
    public Func<Vector2, float, IEnumerable<BuildingPlacement>> StaticPlacementsAround { get; init; } = (_, _) => [];
    /// <summary>
    /// The level data's pickable buildings along a ray (origin, direction, length) with their record footprints; empty without level data.
    /// </summary>
    public Func<Vector3, Vector3, float, IEnumerable<PlacedBuilding>> StaticAlong { get; init; } = (_, _, _) => [];
    /// <summary>
    /// The collision triangles of a building record in its own space, intact or (true) destroyed (a click on a building is tested against them);
    /// empty without level data.
    /// </summary>
    public Func<string, bool, IReadOnlyList<(Vector3[] Vertices, int[] Indices)>> CollisionMesh { get; init; } = (_, _) => [];
    /// <summary>
    /// For each shape of <see cref="CollisionMesh"/>, in order: the index of the door it is a part of, -1 for none.
    /// </summary>
    public Func<string, bool, IReadOnlyList<int>> CollisionMeshDoors { get; init; } = (_, _) => [];
    /// <summary>
    /// Whether a level building is drawn as a ruin now (a town's override, else its save or level state).
    /// </summary>
    public Func<BuildingPlacement, bool> LevelDestroyed { get; init; } = _ => false;
    /// <summary>
    /// The triangles of a collision file by its record path (an item's <c>physics file</c>), in its own space; empty when it is not in the install.
    /// </summary>
    public Func<string, IReadOnlyList<(Vector3[] Vertices, int[] Indices)>> CollisionFile { get; init; } = _ => [];
    /// <summary>
    /// A building without collision's pick stand-in (<see cref="BuildingFootprints.Plot"/>): a click on the ground inside it selects the building.
    /// Never a placement footprint.
    /// </summary>
    public Func<string, BuildingFootprint?> Plot { get; init; } = _ => null;
    /// <summary>
    /// Where a ray (origin, direction, length) first meets a loaded building's floor at or below the viewed floor, and that floor; null for none.
    /// </summary>
    public Func<Vector3, Vector3, float, (Vector3 Position, GhostFloor Floor)?> FloorAlong { get; init; } = (_, _, _) => null;
    /// <summary>
    /// What a BUILDING's ghost reads of the world's resources at a position (<see cref="PlacementRules.Readout"/>); null without them.
    /// </summary>
    public Func<string, Vector3, PlacementReadout?> Readout { get; init; } = (_, _) => null;
    /// <summary>
    /// The bounds box (min, max) of a building part's mesh in its own space; null when it cannot be read (that part then takes no part in the
    /// vertical margins).
    /// </summary>
    public Func<string, (Vector3 Min, Vector3 Max)?> PartBounds { get; init; } = _ => null;
    /// <summary>
    /// The level data's buildings that link together (town walls) within a radius of a point (X, Z): key, record, absolute position and turn; empty
    /// without level data.
    /// </summary>
    public Func<Vector2, float, IEnumerable<(string Key, string Record, Vector3 Position, Quaternion Rotation)>> LinkedAround { get; init; } =
        (_, _) => [];

    /// <summary>
    /// The level data's buildings whose record passes the filter within a radius of a point (X, Z): key, record, absolute position and turn (what a
    /// <c>snaps to</c> ghost snaps to); empty without level data.
    /// </summary>
    public Func<Vector2, float, Func<string, bool>, IEnumerable<(string Key, string Record, Vector3 Position, Quaternion Rotation)>> PlacedAround
    {
        get;
        init;
    } = (_, _, _) => [];

    /// <summary>
    /// The <c>node type</c> of a BUILDING record with <c>is node</c> set (such as <c>Node Light Mount</c>, 5, or <c>Node Turret</c>, 3); null for
    /// any other record.
    /// </summary>
    public Func<string, int?> NodeTypeOf { get; init; } = _ => null;

    /// <summary>
    /// The <c>is node</c> BUILDING instances of a building record in its own local space (cached per record).
    /// </summary>
    public Func<string, IReadOnlyList<PlacedSnapNode>> SnapNodesOf { get; init; } = _ => [];

    /// <summary>
    /// Level buildings within a radius of a point (X, Z) that carry at least one <c>is node</c> instance passing the filter, with their placement
    /// transform and ownership metadata (<see cref="SnapHost"/>).
    /// </summary>
    public Func<Vector2, float, Func<string, bool>, IEnumerable<(string Key, string Record, Vector3 Position, Quaternion Rotation, SnapHost Host)>>
        SnapHostsAround
    {
        get;
        init;
    } = (_, _, _) => [];

    /// <summary>
    /// The code mods' placement rules (docs/modding.md "Placement rules"; engine API, not in the original): whether a mod lifts this refusal of the
    /// ghost. Null when no mod has a rule.
    /// </summary>
    public Func<PlacementQuery, PlacementFailure, bool>? Waive { get; init; }

    /// <summary>
    /// The code mods' placement rules: the text of a mod's refusal of a ghost the game itself allows, else null. Null when no mod has a rule.
    /// </summary>
    public Func<PlacementQuery, string?>? Refuse { get; init; }
}

/// <summary>
/// Everything one evaluation reads (the query is pure over it and the services).
/// </summary>
/// <param name="Parts">The building's parts laid out at the origin with no rotation (<c>WorldObjectLayout.Building</c>), for the ground-type
/// check.</param>
public sealed record PlacementQuery(
    BuildingBuildData Building, Vector3 Position, Quaternion Rotation, float Yaw, int Floor, IReadOnlyList<PlacedMesh> Parts,
    IReadOnlyList<QueuedGhost> Queued, IReadOnlyList<CharacterSnapshot> Characters, IReadOnlyList<BuildingSnapshot> Sites,
    IReadOnlyList<PlacementTown> Towns, string PlayerFactionId, PlacementServices Services)
{
    /// <summary>
    /// A piece of a wall line (docs/game/buildings-production.md "Walls"): its slope is its own pitch, not the ground under its box.
    /// </summary>
    public bool WallPiece { get; init; }
    /// <summary>
    /// Sites and queued ghosts (record, position, rotation) that never block this one: the walls at a snapped joint, connectors and wedges.
    /// </summary>
    public Func<string, Vector3, Quaternion, bool>? Ignore { get; init; }
    /// <summary>
    /// The BUILDING records whose sites, queued ghosts and level buildings never block this ghost: the walls it stands on or snaps to, and its
    /// <c>snaps to</c> target (engine choice, docs/game/buildings-production.md "Snapping to a building").
    /// </summary>
    public Func<string, bool>? PassThrough { get; init; }
    /// <summary>
    /// The BUILDING records whose queued ghosts (not their sites) never block this ghost, on top of <see cref="Passes"/>: an unsnapped wall ghost's
    /// connectors and wedges (docs/game/buildings-production.md "Walls").
    /// </summary>
    public Func<string, bool>? QueuedPassThrough { get; init; }
    /// <summary>
    /// The building floor the ghost stands on; null on the terrain.
    /// </summary>
    public GhostFloor? On { get; init; }
    /// <summary>
    /// The height of the raised walkable surface outside the rooms that the ghost stands on (a wall top): its ground, level, instead of the terrain;
    /// null on the terrain or a floor.
    /// </summary>
    public float? Surface { get; init; }
    /// <summary>
    /// The ghost took its <c>snaps to</c> target's place and turn: like a wall piece, it is judged by its own pitch, not by the ground's margins.
    /// </summary>
    public bool Snapped { get; init; }
    /// <summary>
    /// The host building of the <c>is node</c> snap point this ghost snapped onto; null when not snapped to a building's node.
    /// </summary>
    public SnapHost? SnappedOn { get; init; }
    /// <summary>
    /// Whether this ghost requires a snap target (<c>PreviewBuilding_Snapping</c> slot 5, <c>0x1404d0ff0</c>): unsnapped placement fails with
    /// <see cref="PlacementFailure.NeedsSnapNode"/>.
    /// </summary>
    public bool NeedsSnap { get; init; }
    /// <summary>
    /// The ghost's vertical offset from the ground under the cursor (the original's build-mode height offset); only the margins of interior furniture
    /// and nodes read it.
    /// </summary>
    public float Lift { get; init; }

    /// <summary>
    /// The ground height at (x, z) the ghost is measured against: the raised surface it stands on, else the terrain.
    /// </summary>
    public float GroundAt(float x, float z) => Surface ?? Services.Ground(x, z);

    /// <summary>
    /// Whether a building of <paramref name="record"/> at <paramref name="position"/> / <paramref name="rotation"/> (a site, queued ghost or level
    /// building) never blocks this ghost.
    /// </summary>
    public bool Passes(string record, Vector3 position, Quaternion rotation) =>
        PassThrough?.Invoke(record) == true || Ignore?.Invoke(record, position, rotation) == true;

    /// <summary>
    /// Whether a queued ghost never blocks this ghost: it <see cref="Passes"/>, or its record is a <see cref="QueuedPassThrough"/>.
    /// </summary>
    public bool PassesQueued(QueuedGhost queued) =>
        QueuedPassThrough?.Invoke(queued.Building.Id) == true || Passes(queued.Building.Id, queued.Position, queued.Rotation);
}

/// <summary>
/// The host's half of the placement checks (docs/simulation.md "Buildings", "Placement validation"; the messages and their order are
/// docs/game/buildings-production.md "Placement"): the ground, the footprints and the characters. It runs <see cref="PlacementRules.TownBlock"/> and
/// the resource row first and returns the first failure in the original's order. Not decided (so never failed): snap nodes, usage nodes and the floor
/// number above floor 0; a ceiling ghost fails only without a ceiling. The ghost stands on the terrain, on a building's floor
/// (<see cref="PlacementQuery.On"/>) or on a raised surface outside the rooms (<see cref="PlacementQuery.Surface"/>, a wall top); a ceiling ghost
/// hangs under the ceiling over that floor.
/// </summary>
public static class PlacementCheck
{
    #region Checks

    /// <summary>
    /// A person's radius for the characters-in-the-way test (engine choice; the original's node box test is <b>Observed</b> only).
    /// </summary>
    public const float CharacterRadius = 2.5f;
    /// <summary>
    /// A person's height for the characters-in-the-way test (engine choice): one whose feet to this far over them miss the ghost's height, on
    /// the floor below or above it, is not in the way.
    /// </summary>
    const float CharacterHeight = 19;
    /// <summary>
    /// How far above the floor and below its top the ghost's footprint is tested against the building's structure (engine choice): the floor it stands
    /// on and a ceiling it hangs from touch it there.
    /// </summary>
    const float StructureClearance = 2;
    /// <summary>
    /// How far a wall may reach into the ghost's footprint before it blocks (engine choice): furniture set flush against a wall stays placeable.
    /// </summary>
    const float StructureTolerance = 1;
    /// <summary>
    /// How far above the floor the ghost's footprint is tested against the building's drawn roofs (engine choice): the fixtures drawn lower down
    /// (counters, steps) do not block it.
    /// </summary>
    const float RoofClearance = 12;
    /// <summary>
    /// The slope the original refuses outright (the ground's normal Y below 0.2).
    /// </summary>
    const float SteepestNormalY = 0.2f;

    /// <summary>
    /// The first reason the ghost cannot stand (<see cref="GameVerdict"/>); None when it can. The code mods' placement rules (docs/modding.md
    /// "Placement rules", engine API) may lift any of the game's reasons but the town block, which the simulation applies again on Confirm, and may
    /// refuse a ghost the game allows (<see cref="PlacementFailure.RefusedByMod"/>, the mod's text in <paramref name="refusal"/>, else null).
    /// </summary>
    public static PlacementFailure Evaluate(PlacementQuery q, out string? refusal)
    {
        refusal = null;
        var failure = GameVerdict(q);
        if (failure != PlacementFailure.None || q.Services.Refuse is not { } refuse) return failure;

        refusal = refuse(q);
        return refusal is null ? PlacementFailure.None : PlacementFailure.RefusedByMod;
    }

    /// <summary>
    /// <see cref="Evaluate(PlacementQuery, out string?)"/> without the text of a mod's refusal.
    /// </summary>
    public static PlacementFailure Evaluate(PlacementQuery q) => Evaluate(q, out _);

    /// <summary>
    /// Whether <paramref name="failure"/> keeps the ghost from standing: any reason but None, unless a mod's rule lifts it (never the town block).
    /// </summary>
    static bool Refused(PlacementQuery q, PlacementFailure failure) =>
        failure != PlacementFailure.None
        && (failure == PlacementFailure.TooCloseToTown || q.Services.Waive is not { } waive || !waive(q, failure));

    /// <summary>
    /// The game's own first reason the ghost cannot stand, in the original's order (town, ownership, resources, incomplete site, the centre, inside
    /// only, overlap, ground, slope), each passed over when a mod's rule lifts it; None when it can.
    /// </summary>
    static PlacementFailure GameVerdict(PlacementQuery q)
    {
        // PreviewBuilding_Snapping overrides the placement test and rejects unsnapped snap-only buildings first (0x1404d0ff0).
        if (q.NeedsSnap && !q.Snapped && Refused(q, PlacementFailure.NeedsSnapNode)) return PlacementFailure.NeedsSnapNode;

        var b = q.Building;
        var at = new Vector2(q.Position.X, q.Position.Z);
        // A site floor (or a player site's snap node) counts as ours; a level building as PlacementRules.MayBuildIn has it.
        bool mayBuildIn = q.On is { Level: null }
            || q.On is { } level && PlacementRules.MayBuildIn(b, level.Owned, level.Destroyed, level.OwnerNotReal)
            || q.SnappedOn is { IsLevel: false }
            || q.SnappedOn is { IsLevel: true } sn && PlacementRules.MayBuildIn(b, sn.Owned, sn.Destroyed, sn.OwnerNotReal);
        var town = PlacementRules.TownBlock(b, at, q.Towns, q.PlayerFactionId, mayBuildIn);
        if (Refused(q, town)) return town;

        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, q.Yaw);
        var hull = q.Services.Footprint(b.Id) is { } footprint ? footprint.Place(q.Position, turned) : null;
        // On a floor or a building's snap node: a level building we may build in, or a finished site.
        string? parent = q.On?.Building ?? q.SnappedOn?.Id;
        if (NotOwned(q, parent, mayBuildIn) && Refused(q, PlacementFailure.NotOwnedBuilding)) return PlacementFailure.NotOwnedBuilding;
        // The resource row comes after the first row's "don't own" text and before "incomplete".
        var resources = PlacementRules.ResourceBlock(q.Services.Readout(b.Id, q.Position));
        if (Refused(q, resources)) return resources;
        // A site of ours, or an owned town house being repaired (a level building that is also a site).
        if ((q.On is not null || q.SnappedOn is not null) && q.Sites.FirstOrDefault(s => s.Id == parent) is { Complete: false }
            && Refused(q, PlacementFailure.InsideIncomplete))
        {
            return PlacementFailure.InsideIncomplete;
        }
        var centre = CentreBlock(q, at, hull, parent);
        if (Refused(q, centre)) return centre;

        // Interior-only furniture must stand on a building's floor; what may also stand outside (exterior furniture or a node) goes either way.
        if (parent is null && b.IsInteriorFurniture && !b.IsExteriorFurniture && !b.IsNode && Refused(q, PlacementFailure.MustBeInside))
            return PlacementFailure.MustBeInside;

        var overlap = hull is null ? PlacementFailure.None : HullBlock(q, hull, at, parent);
        if (Refused(q, overlap)) return overlap;

        var ground = GroundBlock(q, b);
        if (Refused(q, ground)) return ground;

        var slope = SlopeBlock(q, b, at, parent);
        return Refused(q, slope) ? slope : PlacementFailure.None;
    }

    /// <summary>
    /// Whether the ghost stands on a floor or snap node of a building it may not build in: a level building it does not own, or no site of ours.
    /// </summary>
    static bool NotOwned(PlacementQuery q, string? parent, bool mayBuildIn) =>
        q.On is { Level: not null } && !mayBuildIn
        || q.On is { Level: null } && q.Sites.FirstOrDefault(s => s.Id == parent) is null
        || q.SnappedOn is { IsLevel: true } && !mayBuildIn
        || q.SnappedOn is { IsLevel: false } && q.Sites.FirstOrDefault(s => s.Id == parent) is null;

    /// <summary>
    /// A building under the ghost's centre <paramref name="at"/> that blocks it; None when there is none.
    /// </summary>
    static PlacementFailure CentreBlock(PlacementQuery q, Vector2 at, Vector2[]? hull, string? parent)
    {
        var ghostFootprint = q.Services.Footprint(q.Building.Id);
        // A building under the ghost's centre (the one it stands in aside): another town's or the level's (not ours), or an unfinished site of ours.
        foreach (var site in q.Sites)
        {
            if (site.Id != parent
                && !OtherFloor(q, parent, site.Parent, site.Floor)
                && !q.Passes(site.Record, site.Position, site.Rotation)
                && q.Services.Footprint(site.Record) is { } siteFootprint
                && (q.SnappedOn is null && !site.Outside || OverlapsHeight(q.Position.Y, ghostFootprint, q.Building.BuildingHeight, site.Position.Y, siteFootprint))
                && Contains(siteFootprint.Place(site.Position, site.Rotation), at)
                && Reaches(q, hull, siteFootprint, site.Position, site.Rotation))
                return site.Complete ? PlacementFailure.TooCloseToBuilding : PlacementFailure.InsideIncomplete;
        }
        foreach (var other in q.Services.StaticAround(at, 20))
            if (Blocks(q, other, parent) && Contains(other.Hull, at)) return PlacementFailure.NotOwnedBuilding;
        return PlacementFailure.None;
    }

    /// <summary>
    /// Whether a level building may block the ghost: not the one it stands in (<paramref name="parent"/>), nor one it passes (a record it passes
    /// through, or a town wall at a wall piece's snapped joint, as the original's joint test takes any wall).
    /// </summary>
    static bool Blocks(PlacementQuery q, PlacedFootprint other, string? parent) =>
        other.Id != parent && !q.Passes(other.Record, other.Position, other.Rotation);

    /// <summary>
    /// What the ghost's footprint <paramref name="hull"/> overlaps: the floor's edge, a building, a site, a queued ghost or a character;
    /// None when nothing.
    /// </summary>
    static PlacementFailure HullBlock(PlacementQuery q, Vector2[] hull, Vector2 at, string? parent)
    {
        // Engine stand-in for the original's node boxes against the building's walls: on a floor the footprint stays within the building's
        // indoor hull.
        if (q.On is { } on && hull.Any(p => Distance(on.Footprint, p) > 0)) return PlacementFailure.TooCloseToBuilding;
        if (CutsStructure(q, hull)) return PlacementFailure.TooCloseToBuilding;
        var ghostFootprint = q.Services.Footprint(q.Building.Id);
        float reach = hull.Max(p => Vector2.Distance(p, at)) + 150;
        foreach (var other in q.Services.StaticAround(at, reach))
        {
            if (Blocks(q, other, parent) && Overlap(hull, other.Hull) && Reaches(q, hull, q.Services.Footprint(other.Record), other.Position, other.Rotation))
                return PlacementFailure.TooCloseToBuilding;
        }

        foreach (var site in q.Sites)
        {
            if (site.Id != parent
                && !OtherFloor(q, parent, site.Parent, site.Floor)
                && !q.Passes(site.Record, site.Position, site.Rotation)
                && q.Services.Footprint(site.Record) is { } f
                && (q.SnappedOn is null && !site.Outside || OverlapsHeight(q.Position.Y, ghostFootprint, q.Building.BuildingHeight, site.Position.Y, f))
                && Overlap(hull, f.Place(site.Position, site.Rotation))
                && Reaches(q, hull, f, site.Position, site.Rotation))
                return PlacementFailure.TooCloseToBuilding;
        }
        foreach (var queued in q.Queued)
        {
            if (queued.Hull is not null
                && !OtherFloor(q, parent, queued.Parent, queued.Floor)
                && !q.PassesQueued(queued)
                && (q.SnappedOn is null && !queued.Outside || OverlapsHeight(q.Position.Y, ghostFootprint, q.Building.BuildingHeight, queued.Position.Y,
                    q.Services.Footprint(queued.Building.Id), queued.Building.BuildingHeight))
                && Overlap(hull, queued.Hull))
                return PlacementFailure.TooCloseToBuilding;
        }
        return CharacterBlock(q, hull);
    }

    /// <summary>
    /// Whether the ghost's vertical span overlaps another building's vertical span (used for elevated snap-node attachments such as exterior wall
    /// lights so floor-level furniture inside the host building does not block a light mounted high on the wall, while two lights on the same node
    /// still collide).
    /// </summary>
    static bool OverlapsHeight(float ghostY, BuildingFootprint? ghost, float ghostHeight, float otherY, BuildingFootprint? other,
        float otherHeight = 100)
    {
        float gMin = ghostY + (ghost?.YMin ?? 0), gMax = ghostY + (ghost?.YMax ?? ghostHeight);
        float oMin = otherY + (other?.YMin ?? 0), oMax = otherY + (other?.YMax ?? otherHeight);
        return gMin <= oMax && oMin <= gMax;
    }

    /// <summary>
    /// <see cref="PlacementFailure.CharactersInTheWay"/> when a living, uncarried character stands in the footprint <paramref name="hull"/> at
    /// the ghost's height (from its floor to its top: the footprint's, else the record's <c>building height</c>); None otherwise.
    /// </summary>
    static PlacementFailure CharacterBlock(PlacementQuery q, Vector2[] hull)
    {
        var footprint = q.Services.Footprint(q.Building.Id);
        float bottom = q.Position.Y + MathF.Max(footprint?.YMin ?? 0, 0);
        float top = q.Position.Y + (footprint?.YMax ?? q.Building.BuildingHeight);

        foreach (var c in q.Characters)
        {
            if (c.Body is { Dead: true } || !c.CarriedBy.IsNone) continue;
            if (c.Position.Y > top || c.Position.Y + CharacterHeight < bottom) continue;
            if (Distance(hull, new Vector2(c.Position.X, c.Position.Z)) < CharacterRadius) return PlacementFailure.CharactersInTheWay;
        }

        return PlacementFailure.None;
    }

    /// <summary>
    /// Whether the walls, floors or roofs of the building the ghost stands in cut into its footprint <paramref name="hull"/>, from
    /// <see cref="StructureClearance"/> over its floor to as much under its top (engine stand-in for the original's node boxes against the
    /// building's physics shapes: the indoor hull is convex and takes in the walls, so a footprint inside it still went into them, feedback #399),
    /// and whether its drawn roofs do from <see cref="RoofClearance"/> up.
    /// </summary>
    static bool CutsStructure(PlacementQuery q, Vector2[] hull)
    {
        if (q.On is not { } on || q.Services.Footprint(q.Building.Id) is not { } footprint) return false;

        float floor = q.Position.Y + MathF.Max(footprint.YMin, 0);
        float top = q.Position.Y + footprint.YMax - StructureClearance;
        return Cuts(on.Structure, hull, floor + StructureClearance, top) || Cuts(on.Roof, hull, floor + RoofClearance, top);
    }

    /// <summary>
    /// Whether another building whose footprint overlaps the ghost's blocks it: on a building's floor, only when one of its collision triangles
    /// (<paramref name="other"/> placed at <paramref name="position"/> / <paramref name="rotation"/>) cuts into the ghost's prism, as the original
    /// tests the ghost's node boxes against the physics shapes in 3D (a companion building at the same origin, such as Forgotten Buildings' citadel
    /// ramp, whose ramp runs round the inside wall, does not block the floor it encloses, #1297); elsewhere always (the 2D stand-in), and when
    /// its triangles are not known.
    /// </summary>
    static bool Reaches(PlacementQuery q, Vector2[]? hull, BuildingFootprint? other, Vector3 position, Quaternion rotation)
    {
        if (q.On is null || hull is null || other is not { Triangles.Length: > 0 } || q.Services.Footprint(q.Building.Id) is not { } footprint)
            return true;

        // A flat ghost (a rug) still has a sliver over the floor to meet a wall with.
        float bottom = q.Position.Y + MathF.Max(footprint.YMin, 0) + StructureClearance;
        float top = MathF.Max(q.Position.Y + footprint.YMax - StructureClearance, bottom + StructureTolerance);

        var prism = new FootprintPrism(hull, bottom, top);
        var place = Matrix4x4.CreateFromQuaternion(Normalised(rotation)) * Matrix4x4.CreateTranslation(position);
        var corners = other.Triangles;
        for (int i = 0; i + 2 < corners.Length; i += 3)
        {
            var a = Vector3.Transform(corners[i], place);
            var b = Vector3.Transform(corners[i + 1], place);
            var c = Vector3.Transform(corners[i + 2], place);
            if (prism.Cuts(a, b, c, StructureTolerance)) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="cuts"/> finds something in the prism of <paramref name="hull"/> from <paramref name="bottom"/> to
    /// <paramref name="top"/>; false when it is not known or the prism is empty.
    /// </summary>
    static bool Cuts(Func<FootprintPrism, float, bool>? cuts, Vector2[] hull, float bottom, float top) =>
        cuts is not null && top > bottom && cuts(new FootprintPrism(hull, bottom, top), StructureTolerance);

    /// <summary>
    /// Whether a site or queued ghost stands on another floor of the building <paramref name="parent"/> the ghost is in: the original tests node
    /// boxes in 3D, so what stands upstairs does not block the floor below (#499).
    /// </summary>
    static bool OtherFloor(PlacementQuery q, string? parent, string? otherParent, int otherFloor) =>
        parent is not null && otherParent == parent && otherFloor != q.Floor;

    /// <summary>
    /// The ground and floor the ghost needs: land under its land parts, a ceiling over a ceiling ghost; None when it has them.
    /// </summary>
    static PlacementFailure GroundBlock(PlacementQuery q, BuildingBuildData b)
    {
        // Parts that need land (BUILDING_PART `ground type` LAND, `affects footprint`): their node must not be below the water.
        var world = Matrix4x4.CreateFromQuaternion(Normalised(q.Rotation)) * Matrix4x4.CreateTranslation(q.Position);
        foreach (var part in q.Parts)
        {
            var source = part.Source;
            if (!source.GetBool("affects footprint") || source.GetInt("ground type") != 1) continue;
            float nodeY = Vector3.Transform(part.Transform.Translation, world).Y + source.GetFloat("footprint vertical");
            if (nodeY < q.Services.WaterLevel) return PlacementFailure.InvalidGround;
        }
        // A ceiling ghost finds no ceiling over it: the floor-number text.
        if (b.CeilingPlacement && q.On?.Ceiling is null) return PlacementFailure.InvalidFloor;
        return PlacementFailure.None;
    }

    /// <summary>
    /// The slope test: a wall piece's own pitch, the floor's normal indoors, else the vertical margins and the ground's slope under the ghost.
    /// </summary>
    static PlacementFailure SlopeBlock(PlacementQuery q, BuildingBuildData b, Vector2 at, string? parent)
    {
        // A wall piece pitches with the ground: its own up axis must be within `max slope` of the vertical (Verified, FUN_140429510). A snapped ghost
        // takes its target's pitch and is judged the same way (engine choice).
        if (q.WallPiece || q.Snapped)
        {
            float cosLimit = MathF.Cos(b.MaxSlope * MathF.PI / 180);
            bool tilted = b.MaxSlope > 0 && Vector3.Transform(Vector3.UnitY, Normalised(q.Rotation)).Y < cosLimit;
            return tilted ? PlacementFailure.NotLevel : PlacementFailure.None;
        }

        // On a building's floor the slope block takes one ray at the origin (FUN_1404daa20, Verified; the indoor ghost matches the slope, so a normal
        // further from the vertical than `max slope` fails it). Its 5 degree surface test is not read.
        if (parent is not null)
        {
            bool steep = b.MaxSlope > 0 && q.On?.Normal is { } floorNormal && AngleFromUp(floorNormal) > b.MaxSlope;
            return steep ? PlacementFailure.NotLevel : PlacementFailure.None;
        }
        // The vertical margins, after the nudge the original gives the ghost each frame (Settle); a margin left below 0 fails.
        if (Settle(q) is { After: { Down: < -MarginEpsilon } or { Up: < -MarginEpsilon } }) return PlacementFailure.NotLevel;
        var bounds = q.Services.Footprint(b.Id)?.Bounds ?? (new Vector2(-4), new Vector2(4));
        float slope = SlopeDegrees(q.GroundAt, at, q.Yaw, bounds, out float normalY);
        // max slope fails a ghost only when it matches the slope (FUN_1404daa20, Verified); any other is only refused by the steepness test.
        if (normalY < SteepestNormalY || b.MatchSlope && b.MaxSlope > 0 && slope > b.MaxSlope) return PlacementFailure.NotLevel;
        return PlacementFailure.None;
    }

    /// <summary>
    /// The angle between a normal and straight up, in degrees.
    /// </summary>
    static float AngleFromUp(Vector3 normal) => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(normal), Vector3.UnitY), -1, 1)) * 180 / MathF.PI;

    /// <summary>
    /// The ground's slope under the footprint's box (engine reading of the original's four rays at the rotated bounding-box corners): the angle from
    /// the vertical of the plane through the four corner heights, and its normal's Y.
    /// </summary>
    public static float SlopeDegrees(Func<float, float, float> ground, Vector2 centre, float yaw, (Vector2 Min, Vector2 Max) box, out float normalY)
    {
        var corners = new Vector3[4];
        var local = new[]
        {
            new Vector2(box.Min.X, box.Min.Y),
            new Vector2(box.Max.X, box.Min.Y),
            new Vector2(box.Max.X, box.Max.Y),
            new Vector2(box.Min.X, box.Max.Y),
        };
        float sin = MathF.Sin(yaw), cos = MathF.Cos(yaw);
        for (int i = 0; i < 4; i++)
        {
            // Yaw about +Y as Quaternion.CreateFromAxisAngle turns a point (x, z): x' = x cos + z sin, z' = -x sin + z cos.
            float x = local[i].X * cos + local[i].Y * sin + centre.X, z = -local[i].X * sin + local[i].Y * cos + centre.Y;
            corners[i] = new Vector3(x, ground(x, z), z);
        }
        var normal = Vector3.Cross(corners[2] - corners[0], corners[3] - corners[1]);
        if (normal.LengthSquared() < 1e-9f)
        {
            normalY = 1;
            return 0;
        }
        normal = Vector3.Normalize(normal);
        if (normal.Y < 0) normal = -normal;
        normalY = normal.Y;
        return MathF.Acos(Math.Clamp(normal.Y, -1, 1)) * 180 / MathF.PI;
    }

    /// <summary>
    /// The ground normal at <paramref name="centre"/> from the box's corners (up when flat), for tilting a ghost to the ground.
    /// </summary>
    public static Vector3 GroundNormal(Func<float, float, float> ground, Vector2 centre, float yaw, (Vector2 Min, Vector2 Max) box)
    {
        var corners = new Vector3[4];
        var local = new[]
        {
            new Vector2(box.Min.X, box.Min.Y),
            new Vector2(box.Max.X, box.Min.Y),
            new Vector2(box.Max.X, box.Max.Y),
            new Vector2(box.Min.X, box.Max.Y),
        };
        float sin = MathF.Sin(yaw), cos = MathF.Cos(yaw);
        for (int i = 0; i < 4; i++)
        {
            float x = local[i].X * cos + local[i].Y * sin + centre.X, z = -local[i].X * sin + local[i].Y * cos + centre.Y;
            corners[i] = new Vector3(x, ground(x, z), z);
        }
        var normal = Vector3.Cross(corners[2] - corners[0], corners[3] - corners[1]);
        if (normal.LengthSquared() < 1e-9f) return Vector3.UnitY;
        normal = Vector3.Normalize(normal);
        return normal.Y < 0 ? -normal : normal;
    }

    /// <summary>
    /// The text of a failure (<see cref="PlacementRules.Message"/>); a mod's refusal shows its own <paramref name="refusal"/>.
    /// </summary>
    public static string Message(PlacementFailure failure, string? refusal = null) =>
        failure == PlacementFailure.RefusedByMod ? refusal ?? "" : PlacementRules.Message(failure);

    #endregion

    #region Vertical margins

    /// <summary>
    /// The value of a margin with nothing limiting it (the original starts both at 1e8).
    /// </summary>
    public const float NoLimit = 1e8f;
    /// <summary>
    /// The terrain height the original never goes below under a node (<c>FUN_1409b2a40</c> / <c>FUN_1404d2080</c>: the sea floor reads as 100).
    /// </summary>
    const float LowestGround = 100;
    /// <summary>
    /// Slack for the margin test, since Settle's own arithmetic is repeated on a recomputed position (engine choice; the original adjusts the margins
    /// instead of measuring again).
    /// </summary>
    const float MarginEpsilon = 1e-3f;

    /// <summary>
    /// How far a ghost can still move, over its footprint parts' nodes: <paramref name="Down"/> is the original's +0x80 (the minimum of node height -
    /// ground over the groups marked <c>above ground</c>: how far it may sink) and <paramref name="Up"/> its +0x84 (the minimum of ground - node
    /// height over the others: how far it may rise).
    /// </summary>
    public readonly record struct VerticalMargins(float Down, float Up)
    {
        /// <summary>
        /// Both margins at <see cref="NoLimit"/>: nothing limits the ghost.
        /// </summary>
        public static VerticalMargins Unlimited => new(NoLimit, NoLimit);
    }

    /// <summary>
    /// The vertical margins of a ghost on the terrain (docs/game/buildings-production.md "Placement", Vertical margins; <c>FUN_1404d9c00</c>,
    /// <c>FUN_1404d21e0</c>, <c>FUN_1404d23c0</c>). A group is a BUILDING_PART with <c>affects footprint</c>: its nodes are the four corners of its
    /// mesh's bounds box (X, Z) at <c>footprint vertical</c> (all times the building's <c>scale</c>), moved by the part's node position and the
    /// ghost's turn and position; the ground under a node is the terrain (the height of the raised surface a ghost stands on), never below 100.
    /// When no group is above ground, the first group is measured again moved up by <c>vertical position tolerance</c> (times scale) the other way
    /// round and joins <see cref="VerticalMargins.Down"/>; when every group is, moved down and joined to <see cref="VerticalMargins.Up"/>. Null for
    /// what the original does not measure: a ghost on a building's floor (Meitou takes a floor as level), a wall and a snapped ghost (their own
    /// pitch test), and interior-only furniture and nodes.
    /// </summary>
    public static VerticalMargins? Margins(PlacementQuery q)
    {
        var b = q.Building;
        bool unmeasured = q.On is not null || q.WallPiece || q.Snapped || b.LinksTogether;
        if (unmeasured || b.IsInteriorFurniture && !b.IsExteriorFurniture && !b.IsNode) return null;
        var rotation = Normalised(q.Rotation);
        var groups = new List<(bool Above, Vector3 Node, Vector3[] Corners)>();
        foreach (var part in q.Parts)
        {
            var source = part.Source;
            if (!source.GetBool("affects footprint") || q.Services.PartBounds(part.MeshPath) is not { } box) continue;
            float y = source.GetFloat("footprint vertical") * b.Scale;
            Vector3 Corner(float x, float z) => new(x * b.Scale, y, z * b.Scale);
            groups.Add((source.GetBool("above ground"), part.Transform.Translation,
                [Corner(box.Min.X, box.Min.Z), Corner(box.Max.X, box.Min.Z), Corner(box.Max.X, box.Max.Z), Corner(box.Min.X, box.Max.Z)]));
        }
        // A group's smallest margin: ground - node height (the node must stay under the ground), or node height - ground when measuring an above
        // ground group; shifted up by `shift`.
        float Measure((bool Above, Vector3 Node, Vector3[] Corners) group, float shift, bool above)
        {
            float least = NoLimit;
            var lift = Vector3.Transform(new Vector3(0, shift * b.Scale, 0), rotation);
            foreach (var corner in group.Corners)
            {
                var world = Vector3.Transform(corner + group.Node, rotation) + q.Position + lift;
                float ground = MathF.Max(q.GroundAt(world.X, world.Z), LowestGround);
                least = MathF.Min(least, above ? world.Y - ground : ground - world.Y);
            }
            return least;
        }
        float down = NoLimit, up = NoLimit;
        foreach (var group in groups)
        {
            float margin = Measure(group, 0, group.Above);
            if (group.Above) down = MathF.Min(down, margin); else up = MathF.Min(up, margin);
        }
        float tolerance = b.VerticalPositionTolerance;
        if (groups.Count > 0 && MathF.Abs(tolerance) > 0)
        {
            if (!groups.Exists(g => g.Above)) down = MathF.Min(down, Measure(groups[0], tolerance, above: true));
            if (groups.TrueForAll(g => g.Above)) up = MathF.Min(up, Measure(groups[0], -MathF.Abs(tolerance), above: false));
        }
        return new VerticalMargins(down, up);
    }

    /// <summary>
    /// The nudge the original gives the ghost's height each frame (<c>FUN_1404d1680</c>, applied in <c>FUN_1404dba40</c>) and the margins after it.
    /// Outdoors: when the ghost cannot fit (down + up below 0) it centres between the two, else a negative down margin raises it by that much and a
    /// negative up margin lowers it. Interior furniture and nodes standing outside are pulled back to no lift, keeping 0.01 under the ground and
    /// inside the down margin. Null when <see cref="Margins"/> measures nothing.
    /// </summary>
    public static (float Step, VerticalMargins After)? Settle(PlacementQuery q)
    {
        if (Margins(q) is not { } m) return null;
        var b = q.Building;
        float step;
        if (b.IsInteriorFurniture || b.IsNode)
        {
            step = MathF.Min(-q.Lift, m.Up - 0.01f);
            step = m.Up - step < 0 ? m.Up : m.Down + step < 0 ? -m.Down : step;
        }
        else
            step = m.Down + m.Up < 0 ? (m.Up - m.Down) * 0.5f : m.Down < 0 ? -m.Down : m.Up < 0 ? m.Up : 0;
        return (step, new VerticalMargins(m.Down + step, m.Up - step));
    }

    #endregion

    #region Plane geometry on convex polygons

    /// <summary>
    /// The quaternion at unit length; the identity for a near-zero one.
    /// </summary>
    static Quaternion Normalised(Quaternion q) => q.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(q);

    /// <summary>
    /// The turn about +Y of a rotation (the angle <c>Quaternion.CreateFromAxisAngle(UnitY, a)</c> would give for its effect on a point's X and Z).
    /// </summary>
    public static float YawOf(Quaternion rotation)
    {
        var forward = Vector3.Transform(Vector3.UnitX, Normalised(rotation));
        // CreateFromAxisAngle(UnitY, a) takes (1, 0, 0) to (cos a, 0, -sin a).
        return MathF.Atan2(-forward.Z, forward.X);
    }

    /// <summary>
    /// Whether the point is inside the convex polygon or on its edge.
    /// </summary>
    internal static bool Contains(Vector2[] polygon, Vector2 point) => Distance(polygon, point) <= 0;

    /// <summary>
    /// The distance from a point to a convex polygon, 0 inside it (either winding).
    /// </summary>
    static float Distance(Vector2[] polygon, Vector2 point)
    {
        float sign = Winding(polygon), outside = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var e = polygon[(i + 1) % polygon.Length] - a;
            float length = e.Length();
            if (length < 1e-6f) continue;
            outside = Math.Max(outside, -sign * (e.X * (point.Y - a.Y) - e.Y * (point.X - a.X)) / length);
        }
        return outside;
    }

    /// <summary>
    /// The winding of a polygon: 1 when its signed area is positive or zero, else -1. Summed from its first vertex: at world coordinates (~1e5) the
    /// float products round by hundreds of square units, more than a chest's whole footprint, so the winding came out at random and such a
    /// polygon could never contain a point.
    /// </summary>
    internal static float Winding(Vector2[] polygon)
    {
        if (polygon.Length < 3) return 1;

        float area = 0;
        var origin = polygon[0];
        for (int i = 1; i + 1 < polygon.Length; i++)
        {
            var a = polygon[i] - origin;
            var b = polygon[i + 1] - origin;
            area += a.X * b.Y - b.X * a.Y;
        }

        return area >= 0 ? 1 : -1;
    }

    /// <summary>
    /// Whether two convex polygons overlap (the separating axis test on their edge normals).
    /// </summary>
    public static bool Overlap(Vector2[] a, Vector2[] b)
    {
        foreach (var polygon in (Vector2[][])[a, b])
            for (int i = 0; i < polygon.Length; i++)
            {
                var edge = polygon[(i + 1) % polygon.Length] - polygon[i];
                var axis = new Vector2(-edge.Y, edge.X);
                if (axis.LengthSquared() < 1e-9f) continue;
                Project(a, axis, out float aMin, out float aMax);
                Project(b, axis, out float bMin, out float bMax);
                if (aMax <= bMin || bMax <= aMin) return false;
            }
        return true;
    }

    /// <summary>
    /// The interval the polygon's vertices project to on <paramref name="axis"/>.
    /// </summary>
    static void Project(Vector2[] polygon, Vector2 axis, out float min, out float max)
    {
        min = float.MaxValue;
        max = float.MinValue;
        foreach (var p in polygon)
        {
            float d = Vector2.Dot(p, axis);
            min = Math.Min(min, d);
            max = Math.Max(max, d);
        }
    }

    #endregion
}