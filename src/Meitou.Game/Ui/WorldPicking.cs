using System.Numerics;
using Meitou.Game.Building;
using Meitou.Simulation;

namespace Meitou.Game.Ui;

/// <summary>Picking: the ray through a pixel, its floor or terrain destination, which character it touches, the box select.</summary>
sealed class WorldPicking(PlayerInterface player)
{
    // ---- geometry ----

    /// <summary>The host's building-floor pick, limited to the terrain hit's distance.</summary>
    public Func<Vector3, Vector3, float, Vector3?>? BuildingMoveHit { get; set; }

    /// <summary>The host's pick of a raised walkable surface outside the rooms (a wall top, a ramp, a deck), limited to a distance.</summary>
    public Func<Vector3, Vector3, float, Vector3?>? SurfaceMoveHit { get; set; }

    /// <summary>The movement destination on a raised walkable surface or a building floor, or the terrain when neither is in front of it.</summary>
    public Vector3? MoveHit(Vector3 origin, Vector3 direction)
    {
        var terrain = GroundHit(origin, direction);
        float distance = terrain is { } hit ? Vector3.Distance(origin, hit) : 40000;
        var room = BuildingMoveHit?.Invoke(origin, direction, distance);
        if (room is { } floor) distance = Vector3.Distance(origin, floor);
        return SurfaceMoveHit?.Invoke(origin, direction, distance) ?? room ?? terrain;
    }

    /// <summary>Debug: called with the ground point under the cursor (null when none) on a left press that no widget took; true consumes the press (the host's "Explode on click").</summary>
    public Func<Vector3?, bool>? WorldClick { get; set; }

    /// <summary>Where the ray meets the ground: marched in growing steps, then bisected. Null when it leaves the world first.</summary>
    public Vector3? GroundHit(Vector3 origin, Vector3 direction, float maxDistance = 40000)
    {
        float Above(float t)
        {
            var p = origin + direction * t;
            return p.Y - player.Ground(p.X, p.Z);
        }
        if (Above(0) <= 0) return origin;
        float previous = 0, step = 2;
        for (float t = step; t <= maxDistance; t += step)
        {
            if (Above(t) <= 0)
            {
                float lo = previous, hi = t;
                for (int i = 0; i < 24; i++)
                {
                    float mid = (lo + hi) / 2;
                    if (Above(mid) > 0) lo = mid; else hi = mid;
                }
                var p = origin + direction * hi;
                return new Vector3(p.X, player.Ground(p.X, p.Z), p.Z);
            }
            previous = t;
            step = MathF.Max(2, t * 0.01f);
        }
        return null;
    }

    /// <summary>The distance along the ray at which it passes closest to the segment a-b, when within <paramref name="radius"/>.</summary>
    internal static float? RaySegment(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, float radius)
    {
        var u = direction;
        var v = b - a;
        var w = origin - a;
        float vv = Vector3.Dot(v, v), uv = Vector3.Dot(u, v), uw = Vector3.Dot(u, w), vw = Vector3.Dot(v, w);
        float denominator = vv - uv * uv;   // |u| = 1
        float s = denominator < 1e-6f ? 0 : Math.Clamp((uv * vw - vv * uw) / denominator, 0, 1e9f);
        float q = vv < 1e-6f ? 0 : Math.Clamp((vw + s * uv) / vv, 0, 1);
        s = MathF.Max(0, uv * q - uw);
        var onRay = origin + u * s;
        var onSegment = a + v * q;
        return Vector3.Distance(onRay, onSegment) <= radius ? s : null;
    }

    /// <summary>The player's character under the pixel (capsule from the feet up, a little fatter with distance), if any.</summary>
    public CharacterId? Pick(Vector2 pixel) => PickWhere(pixel, c => c.IsPlayer);

    /// <summary>A living character of anyone else's under the pixel: what a right click attacks (hostility is not checked yet).</summary>
    public CharacterId? PickTarget(Vector2 pixel) => PickWhere(pixel, c => !c.IsPlayer && c.Standing);

    /// <summary>A living trader under the pixel (a member of a squad with a shop): a right click trades with it, or attacks it when its faction is hostile (the world decides).</summary>
    public CharacterId? PickTrader(Vector2 pixel) => PickWhere(pixel, c => c.IsTrader && c.Standing);

    /// <summary>A knocked-out or dead character under the pixel that nobody carries (what a pick-up or a loot is for); <paramref name="ownOnly"/> takes only the player's.</summary>
    public CharacterId? PickDowned(Vector2 pixel, bool ownOnly = false) =>
        PickWhere(pixel, c => c.Body is { } b && (b.Unconscious || b.Dead) && c.CarriedBy.IsNone && (!ownOnly || c.IsPlayer)) ?? (ownOnly ? null : PickWhere(pixel, c => !c.CarriedBy.IsNone));

    /// <summary>One of the player's own living characters under the pixel that first aid has something to bandage on (a right click on it orders the selection to treat it).</summary>
    public CharacterId? PickHurtAlly(Vector2 pixel) => PickWhere(pixel, c => c.IsPlayer && c.Body is { Dead: false, NeedsAid: true });

    /// <summary>
    /// The renderer's test of a ray against a character's posed mesh (<c>CharacterRenderer.RayHit</c>): the distance, +infinity for a miss, null when it does not draw
    /// that character (the capsule decides then). Null without a renderer.
    /// </summary>
    public Func<CharacterId, Vector3, Vector3, float?>? MeshHit { get; set; }

    internal CharacterId? PickWhere(Vector2 pixel, Func<CharacterSnapshot, bool> accept) => PickWhereAt(pixel, accept)?.Id;

    /// <summary>The nearest accepted character under the pixel with its ray distance; null when none, or when the nearest is hidden behind a building seen from outside.</summary>
    internal (CharacterId Id, float T)? PickWhereAt(Vector2 pixel, Func<CharacterSnapshot, bool> accept)
    {
        if (player.Ray(pixel) is not { } ray) return null;
        CharacterId? best = null;
        float bestT = float.MaxValue;
        foreach (var c in player.Session.CurrentSnapshot.Characters)
        {
            if (!accept(c) || player.StealthView.HiddenInWorld?.Invoke(c.Id) == true) continue;   // not drawn: not picked, as the original's pick needs a visible character
            // The drawn body where the renderer has it (every part of a person or an animal, as posed); else a capsule from the feet up.
            float? hit = MeshHit?.Invoke(c.Id, ray.Origin, ray.Direction);
            if (hit is { } meshT)
            {
                if (meshT < bestT) (bestT, best) = (meshT, c.Id);
                continue;
            }
            var feet = player.Where(c);
            float distance = Vector3.Distance(player.Eye, feet);
            float radius = MathF.Max(1.0f, distance * 0.012f);
            if (RaySegment(ray.Origin, ray.Direction, feet, feet + new Vector3(0, PlayerInterface.CharacterHeight, 0), radius) is { } t && t < bestT)
            {
                bestT = t;
                best = c.Id;
            }
        }
        (CharacterId Id, float T)? picked = best is not { } found || player.Hidden(ray, bestT) ? null : (found, bestT);
        // An occupied cage with occupant selection that the ray meets first gives its occupant (the original's FUN_1407fb410 for the cage's occupant).
        if (player.Furniture.PickCageAt(pixel) is { Cage: { OccupantSelection: true, Occupant: { IsNone: false } occupant }, T: var cageT } && (picked is not { } nearer || cageT < nearer.T)
            && player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == occupant) is { } held && accept(held) && player.StealthView.HiddenInWorld?.Invoke(occupant) != true)
            return (occupant, cageT);
        return picked;
    }

    /// <summary>What a right click acts on, as <see cref="PickAt"/> chose it.</summary>
    internal enum Picked { None, Character, GroundItem, Container, Door, Rock, Site, Bed, Cage, Dummy, Seat }

    /// <summary>
    /// The right button's target under the pixel, with characters selected (docs/game/ui-input.md "Building picking and selection"): the nearest along the ray of a character with a quick
    /// order, a ground item, a container, a door, a rock and a site that is an object (unfinished, a machine, storage, furnace or turret), as the original's pick walks its hits nearest first.
    /// What a building seen from outside hides is left out (each
    /// helper's <see cref="PlayerInterface.Hidden"/> test, the original's floor rule). A plain building is not a candidate: it takes the click only when nothing is (<see cref="BuildMode.SitePress"/>). Hover and click
    /// share it, as in the original.
    /// </summary>
    internal (Picked Kind, float T) PickAt(Vector2 p)
    {
        (Picked Kind, float T) best = (Picked.None, float.MaxValue);
        void Offer(Picked kind, float? t) { if (t is { } v && v < best.T) best = (kind, v); }
        if (player.QuickOrder(p) is { Do: not null } order) Offer(Picked.Character, PickWhereAt(p, c => c.Id == order.Target)?.T ?? 0);
        else if (PickWhereAt(p, _ => true) is { } any && player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == any.Id) is { } held && player.ContextMenuWindow.Verbs(held).Count > 0)
            Offer(Picked.Character, any.T);   // no quick order, but a held click opens its menu
        var item = player.GroundItemPicks.PickGroundItemAt(p);
        Offer(Picked.GroundItem, item?.T);
        var container = player.Furniture.PickContainerAt(p);
        Offer(Picked.Container, container?.T);
        Offer(Picked.Door, player.Furniture.PickDoorAt(p)?.T);
        Offer(Picked.Seat, player.Furniture.PickSeatAt(p)?.T);
        var bed = player.Furniture.PickBedAt(p);
        Offer(Picked.Bed, bed?.T);
        var cage = player.Furniture.PickCageAt(p);
        Offer(Picked.Cage, cage?.T);
        Offer(Picked.Dummy, player.Furniture.PickDummyAt(p)?.T);
        Offer(Picked.Rock, player.Furniture.PickRockAt(p)?.T);
        if (player.Build.PickSiteAt(p) is { } site && player.Build.SiteIsObject(site.Site)) Offer(Picked.Site, site.T);
        // An item behind the container, bed or cage met first but lying inside its bounds takes the click (the original's walk, FUN_140800250: the building's box holds the item).
        if (item is { } i && ItemAt(i.Id) is { } at && best.Kind switch
        {
            Picked.Container => Holds(container!.Value.Container.Record, container.Value.Container.Position, container.Value.Container.Yaw, at),
            Picked.Bed => Holds(bed!.Value.Bed.Record, bed.Value.Bed.Position, bed.Value.Bed.Yaw, at),
            Picked.Cage => Holds(cage!.Value.Cage.Record, cage.Value.Cage.Position, cage.Value.Cage.Yaw, at),
            _ => false,
        })
            best = (Picked.GroundItem, i.T);
        return best;
    }

    /// <summary>Where a ground item is drawn (<see cref="GroundItemPicks.GroundItemShape"/>), else where it lies; null when it is gone.</summary>
    Vector3? ItemAt(int id)
    {
        foreach (var g in player.Session.CurrentSnapshot.GroundItems)
            if (g.Id == id) return player.GroundItemPicks.GroundItemShape?.Invoke(g)?.At ?? g.Position;
        return null;
    }

    /// <summary>Whether a point lies inside the box of a building's collision placed at <paramref name="position"/> turned by <paramref name="yaw"/> (its footprint, from its lowest to its highest point).</summary>
    bool Holds(string record, Vector3 position, float yaw, Vector3 point) =>
        player.Build.Services?.Placement.Footprint(record) is { } f && point.Y >= position.Y + f.YMin && point.Y <= position.Y + f.YMax
        && f.Gap(position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw), new Vector2(point.X, point.Z)) == 0;

    /// <summary>The host's test for where a ray enters a building seen from outside (its interior not shown), within a distance; null when none is in the way.</summary>
    public Func<Vector3, Vector3, float, float?>? ClosedBuildingHit { get; set; }

    /// <summary>The host's test for where a ray meets a shown floor or wall of a building in the interior view, within a distance; null when none is in the way.</summary>
    public Func<Vector3, Vector3, float, float?>? ShownInteriorHit { get; set; }

    internal IReadOnlyList<CharacterId> InBox(Vector2 a, Vector2 b)
    {
        float x0 = MathF.Min(a.X, b.X), x1 = MathF.Max(a.X, b.X), y0 = MathF.Min(a.Y, b.Y), y1 = MathF.Max(a.Y, b.Y);
        var list = new List<CharacterId>();
        foreach (var c in player.Session.CurrentSnapshot.Characters)
        {
            if (!c.IsPlayer) continue;
            var p = player.Where(c) + new Vector3(0, PlayerInterface.CharacterHeight / 2, 0);
            if (Vector3.Distance(player.Eye, p) > PlayerInterface.BoxSelectRange) continue;
            if (player.Project(p) is { } s && s.X >= x0 && s.X <= x1 && s.Y >= y0 && s.Y <= y1) list.Add(c.Id);
        }
        return list;
    }

    // Picking buildings against their collision triangles (docs/game/ui-input.md "Building picking and selection"): the original's pick is a raycast against the collision shapes, not a prism
    // over their merged hull. The hull prism stays as the first, cheap test; a building that has collision files is then tested triangle by triangle, so ground, characters and neighbours
    // next to a building are not taken for it.
    readonly Dictionary<string, IReadOnlyList<(Vector3[] Vertices, int[] Indices)>> _collisionMeshes = [];

    IReadOnlyList<(Vector3[] Vertices, int[] Indices)> CollisionMeshOf(string record)
    {
        if (_collisionMeshes.TryGetValue(record, out var known)) return known;
        var mesh = player.Build.Services?.Placement.CollisionMesh(record) ?? [];
        _collisionMeshes[record] = mesh;
        return mesh;
    }

    /// <summary>
    /// Where the ray meets a building: its hull prism first (null: no hit), then, when the record has collision triangles, the nearest of
    /// those (null: the ray passes through the prism without touching the building). <paramref name="y"/> is the building's base height.
    /// </summary>
    internal float? BuildingRayT((Vector3 Origin, Vector3 Direction) ray, Vector2[] hull, float yMin, float yMax, string? record, Vector3 position, float y, Quaternion rotation)
    {
        if (BuildMode.RayPrism(ray.Origin, ray.Direction, hull, y + yMin, y + yMax) is not { } prism) return null;
        if (record is null) return prism;
        var mesh = CollisionMeshOf(record);
        return mesh.Count == 0 ? prism : RayMesh(ray, mesh, new Vector3(position.X, y, position.Z), rotation);
    }

    /// <summary>
    /// Where the ray meets a building without collision (a farm) by its plot (<see cref="Building.PlacementServices.Plot"/>, an engine stand-in): just before the ground, when the ground
    /// the ray meets at <paramref name="ground"/> lies inside the placed hull; null otherwise.
    /// </summary>
    internal static float? PlotT((Vector3 Origin, Vector3 Direction) ray, Vector2[] hull, float? ground)
    {
        if (ground is not { } t) return null;
        var at = ray.Origin + ray.Direction * t;
        // Inside a convex polygon of either winding: on the same side of every edge.
        float sign = 0;
        for (int i = 0; i < hull.Length; i++)
        {
            var e = hull[(i + 1) % hull.Length] - hull[i];
            float side = e.X * (at.Z - hull[i].Y) - e.Y * (at.X - hull[i].X);
            if (side == 0) continue;
            if (sign == 0) sign = MathF.Sign(side);
            else if (MathF.Sign(side) != sign) return null;
        }
        return t - 0.01f;
    }

    /// <summary>The nearest hit of the ray on the triangles (double sided), the mesh turned by <paramref name="rotation"/> and put at <paramref name="at"/>; null for none.</summary>
    internal static float? RayMesh((Vector3 Origin, Vector3 Direction) ray, IReadOnlyList<(Vector3[] Vertices, int[] Indices)> mesh, Vector3 at, Quaternion rotation)
    {
        var q = rotation.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(rotation);
        var inverse = Quaternion.Conjugate(q);
        var origin = Vector3.Transform(ray.Origin - at, inverse);
        var direction = Vector3.Transform(ray.Direction, inverse);
        float? best = null;
        foreach (var (vertices, indices) in mesh)
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                var a = vertices[indices[t]];
                var e1 = vertices[indices[t + 1]] - a;
                var e2 = vertices[indices[t + 2]] - a;
                var p = Vector3.Cross(direction, e2);
                float determinant = Vector3.Dot(e1, p);
                if (MathF.Abs(determinant) < 1e-9f) continue;
                float inv = 1 / determinant;
                var s = origin - a;
                float u = Vector3.Dot(s, p) * inv;
                if (u < 0 || u > 1) continue;
                var r = Vector3.Cross(s, e1);
                float v = Vector3.Dot(direction, r) * inv;
                if (v < 0 || u + v > 1) continue;
                float distance = Vector3.Dot(e2, r) * inv;
                if (distance > 0 && (best is null || distance < best)) best = distance;
            }
        return best;
    }
}