using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Objects;
using Meitou.Simulation.Construction;

namespace Meitou.Game.Building;

/// <summary>
/// The usage node markers of build mode's ghosts (docs/game/buildings-production.md "Usage node markers"): the ghost at the cursor shows every
/// marker of its building (<c>PreviewBuilding::resetUseageNodes</c>), a queued ghost only those whose node box is clear, judged again whenever the
/// queue changes (<c>PreviewBuilding::validateUsageNodes</c>). Every marker is drawn in the valid ghost's green (<c>greenbuilding</c>).
/// </summary>
sealed class UseNodeMarkers
{
    #region Markers

    /// <summary>
    /// The queue as it was when its markers were last judged, by reference.
    /// </summary>
    readonly List<QueuedGhost> _judged = [];

    /// <summary>
    /// The markers each queued ghost shows, by reference (a record compares by value, and two ghosts can be equal).
    /// </summary>
    readonly Dictionary<QueuedGhost, IReadOnlyList<PlacedMesh>> _shown = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Adds the markers of the ghost at the cursor (or a wall piece) of <paramref name="record"/> at <paramref name="world"/>: all of them.
    /// </summary>
    internal static void AddHeld(BuildServices host, string record, Matrix4x4 world, List<WorldObjectRenderer.GhostDraw> draws)
    {
        var markers = host.UseNodeMarkers(record);
        if (markers.Count > 0) draws.Add(new(markers, world, WorldObjectRenderer.GhostState.Valid));
    }

    /// <summary>
    /// Adds the markers the queued ghosts show, judging them again when the queue changed since the last call.
    /// </summary>
    internal void AddQueued(BuildServices host, IReadOnlyList<QueuedGhost> queue, IReadOnlyList<BuildingSnapshot> sites,
        List<WorldObjectRenderer.GhostDraw> draws)
    {
        if (!SameQueue(queue)) Judge(host, queue, sites);

        foreach (var ghost in queue)
        {
            if (_shown.TryGetValue(ghost, out var shown) && shown.Count > 0)
                draws.Add(new(shown, BuildMode.World(ghost.Position, ghost.Rotation), WorldObjectRenderer.GhostState.Valid));
        }
    }

    /// <summary>
    /// Forgets the judged queue (build mode closed).
    /// </summary>
    internal void Clear()
    {
        _judged.Clear();
        _shown.Clear();
    }

    /// <summary>
    /// Whether <paramref name="queue"/> holds the same ghosts, in the same order, as when it was last judged.
    /// </summary>
    bool SameQueue(IReadOnlyList<QueuedGhost> queue)
    {
        if (queue.Count != _judged.Count) return false;
        for (int i = 0; i < queue.Count; i++)
            if (!ReferenceEquals(queue[i], _judged[i])) return false;

        return true;
    }

    /// <summary>
    /// Judges every queued ghost's markers (the original's validation after a ghost joins or leaves the queue).
    /// </summary>
    void Judge(BuildServices host, IReadOnlyList<QueuedGhost> queue, IReadOnlyList<BuildingSnapshot> sites)
    {
        _judged.Clear();
        _judged.AddRange(queue);
        _shown.Clear();
        foreach (var ghost in queue) _shown[ghost] = Shown(host, ghost, queue, sites);
    }

    /// <summary>
    /// The markers of <paramref name="ghost"/> whose node box is clear (the full list when all are).
    /// </summary>
    static IReadOnlyList<PlacedMesh> Shown(BuildServices host, QueuedGhost ghost, IReadOnlyList<QueuedGhost> queue,
        IReadOnlyList<BuildingSnapshot> sites)
    {
        var markers = host.UseNodeMarkers(ghost.Building.Id);
        if (markers.Count == 0) return markers;

        var world = BuildMode.World(ghost.Position, ghost.Rotation);
        var shown = markers.Where(m => Clear(host, ghost, m.Transform * world, queue, sites)).ToList();
        return shown.Count == markers.Count ? markers : shown;
    }

    #endregion

    #region Node test

    /// <summary>
    /// The half width of a node's box in X and Z, and its height over the node (<b>Verified</b>, <c>FUN_1404d99b0</c>: extents (-2.5, 0, -2.5) to
    /// (2.5, 10, 2.5)).
    /// </summary>
    const float BoxHalfWidth = 2.5f, BoxHeight = 10;

    /// <summary>
    /// How far a node of a ghost standing outdoors may be from the terrain height under it (<b>Verified</b>, <c>FUN_1404d45a0</c>).
    /// </summary>
    const float GroundReach = 40;

    /// <summary>
    /// How far round a node the level's buildings are read (the largest footprint's reach).
    /// </summary>
    const float StaticReach = 300;

    /// <summary>
    /// Whether the box of a node of <paramref name="ghost"/> at <paramref name="node"/> is clear (<c>FUN_1404d45a0</c>): outdoors within
    /// <see cref="GroundReach"/> of the terrain, and meeting no other queued ghost's footprint nor a building. The original tests the box against
    /// the physics shapes; Meitou against the buildings' collision footprints (sites, and outdoors the level's buildings), their own height span
    /// included (engine choice).
    /// </summary>
    static bool Clear(BuildServices host, QueuedGhost ghost, Matrix4x4 node, IReadOnlyList<QueuedGhost> queue, IReadOnlyList<BuildingSnapshot> sites)
    {
        var at = node.Translation;
        if (ghost.Parent is null && MathF.Abs(host.Placement.Ground(at.X, at.Z) - at.Y) > GroundReach) return false;

        var box = Box(node);
        float top = at.Y + BoxHeight;
        if (HitsQueued(host, ghost, box, at.Y, top, queue)) return false;
        if (HitsSite(host, ghost, box, at.Y, top, sites)) return false;

        return ghost.Parent is not null || !HitsLevel(host, box, at, top);
    }

    /// <summary>
    /// The node box's corners on the ground plane, turned with the node.
    /// </summary>
    static Vector2[] Box(Matrix4x4 node)
    {
        Vector2 Corner(float x, float z)
        {
            var p = Vector3.Transform(new Vector3(x, 0, z), node);
            return new Vector2(p.X, p.Z);
        }

        const float h = BoxHalfWidth;
        return [Corner(-h, -h), Corner(h, -h), Corner(h, h), Corner(-h, h)];
    }

    /// <summary>
    /// Whether the box meets another queued ghost's footprint within its height (the original's other previews' footprint boxes).
    /// </summary>
    static bool HitsQueued(BuildServices host, QueuedGhost ghost, Vector2[] box, float bottom, float top, IReadOnlyList<QueuedGhost> queue)
    {
        foreach (var other in queue)
        {
            if (ReferenceEquals(other, ghost) || other.Hull is not { } hull) continue;

            var footprint = host.Placement.Footprint(other.Building.Id);
            float otherBottom = other.Position.Y + (footprint?.YMin ?? 0);
            float otherTop = other.Position.Y + (footprint?.YMax ?? other.Building.BuildingHeight);
            if (bottom <= otherTop && otherBottom <= top && PlacementCheck.Overlap(box, hull)) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether the box meets a shape of a site's footprint within its height, the site the ghost stands in aside.
    /// </summary>
    static bool HitsSite(BuildServices host, QueuedGhost ghost, Vector2[] box, float bottom, float top, IReadOnlyList<BuildingSnapshot> sites)
    {
        foreach (var site in sites)
        {
            if (site.Id == ghost.Parent || host.Placement.Footprint(site.Record) is not { } footprint) continue;
            if (bottom > site.Position.Y + footprint.YMax || site.Position.Y + footprint.YMin > top) continue;
            if (Shapes(footprint, site.Position, site.Rotation).Any(shape => PlacementCheck.Overlap(box, shape))) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether the box meets a shape of a level building's footprint within its height.
    /// </summary>
    static bool HitsLevel(BuildServices host, Vector2[] box, Vector3 at, float top)
    {
        foreach (var other in host.Placement.StaticAround(new Vector2(at.X, at.Z), StaticReach))
        {
            var footprint = host.Placement.Footprint(other.Record);
            if (footprint is not null && (at.Y > other.Position.Y + footprint.YMax || other.Position.Y + footprint.YMin > top)) continue;

            var shapes = footprint is null ? [other.Hull] : Shapes(footprint, other.Position, other.Rotation);
            if (shapes.Any(shape => PlacementCheck.Overlap(box, shape))) return true;
        }
        return false;
    }

    /// <summary>
    /// A footprint's collision shapes in the world, or its merged hull when it keeps none.
    /// </summary>
    static IEnumerable<Vector2[]> Shapes(Simulation.Navigation.BuildingFootprint footprint, Vector3 position, Quaternion rotation) =>
        footprint.Shapes.Count > 0 ? footprint.PlaceShapes(position, rotation) : [footprint.Place(position, rotation)];

    #endregion
}
