using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>Picking: the ray through a pixel, its floor or terrain destination, which character it touches, the box select.</summary>
sealed partial class PlayerInterface
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

    /// <summary>The picking ray through a pixel: the near-plane point and the unit direction.</summary>
    public (Vector3 Origin, Vector3 Direction)? Ray(Vector2 pixel)
    {
        if (!Matrix4x4.Invert(ViewProjection, out var inverse)) return null;
        Vector3 At(float z)
        {
            var p = Vector4.Transform(new Vector4(pixel.X / Width * 2 - 1, 1 - pixel.Y / Height * 2, z, 1), inverse);
            return new Vector3(p.X, p.Y, p.Z) / p.W;
        }
        var near = At(0);
        var far = At(1);
        return (near, Vector3.Normalize(far - near));
    }

    /// <summary>Where the ray meets the ground: marched in growing steps, then bisected. Null when it leaves the world first.</summary>
    public Vector3? GroundHit(Vector3 origin, Vector3 direction, float maxDistance = 40000)
    {
        float Above(float t)
        {
            var p = origin + direction * t;
            return p.Y - ground(p.X, p.Z);
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
                return new Vector3(p.X, ground(p.X, p.Z), p.Z);
            }
            previous = t;
            step = MathF.Max(2, t * 0.01f);
        }
        return null;
    }

    /// <summary>The distance along the ray at which it passes closest to the segment a-b, when within <paramref name="radius"/>.</summary>
    static float? RaySegment(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, float radius)
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

    /// <summary>A pixel for a world point, or null behind the camera.</summary>
    public Vector2? Project(Vector3 p)
    {
        var clip = Vector4.Transform(new Vector4(p, 1), ViewProjection);
        if (clip.W <= 0.1f) return null;
        return new Vector2((clip.X / clip.W * 0.5f + 0.5f) * Width, (0.5f - clip.Y / clip.W * 0.5f) * Height);
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

    CharacterId? PickWhere(Vector2 pixel, Func<CharacterSnapshot, bool> accept) => PickWhereAt(pixel, accept)?.Id;

    /// <summary>The nearest accepted character under the pixel with its ray distance; null when none, or when the nearest is hidden behind a building seen from outside.</summary>
    (CharacterId Id, float T)? PickWhereAt(Vector2 pixel, Func<CharacterSnapshot, bool> accept)
    {
        if (Ray(pixel) is not { } ray) return null;
        CharacterId? best = null;
        float bestT = float.MaxValue;
        foreach (var c in session.CurrentSnapshot.Characters)
        {
            if (!accept(c) || HiddenInWorld?.Invoke(c.Id) == true) continue;   // not drawn: not picked, as the original's pick needs a visible character
            // The drawn body where the renderer has it (every part of a person or an animal, as posed); else a capsule from the feet up.
            float? hit = MeshHit?.Invoke(c.Id, ray.Origin, ray.Direction);
            if (hit is { } meshT)
            {
                if (meshT < bestT) (bestT, best) = (meshT, c.Id);
                continue;
            }
            var feet = where(c);
            float distance = Vector3.Distance(Eye, feet);
            float radius = MathF.Max(1.0f, distance * 0.012f);
            if (RaySegment(ray.Origin, ray.Direction, feet, feet + new Vector3(0, CharacterHeight, 0), radius) is { } t && t < bestT)
            {
                bestT = t;
                best = c.Id;
            }
        }
        (CharacterId Id, float T)? picked = best is not { } found || Hidden(ray, bestT) ? null : (found, bestT);
        // An occupied cage with occupant selection that the ray meets first gives its occupant (the original's FUN_1407fb410 for the cage's occupant).
        if (PickCageAt(pixel) is { Cage: { OccupantSelection: true, Occupant: { IsNone: false } occupant }, T: var cageT } && (picked is not { } nearer || cageT < nearer.T)
            && session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == occupant) is { } held && accept(held) && HiddenInWorld?.Invoke(occupant) != true)
            return (occupant, cageT);
        return picked;
    }

    /// <summary>What a right click acts on, as <see cref="PickAt"/> chose it.</summary>
    enum Picked { None, Character, GroundItem, Container, Door, Rock, Site, Bed, Cage, Dummy }

    /// <summary>
    /// The right button's target under the pixel, with characters selected (docs/game/ui-input.md "Building picking and selection"): the nearest along the ray of a character with a quick
    /// order, a ground item, a container, a door, a rock and a site that is an object (unfinished, a machine, storage, furnace or turret), as the original's pick walks its hits nearest first.
    /// What a building seen from outside hides is left out (each
    /// helper's <see cref="Hidden"/> test, the original's floor rule). A plain building is not a candidate: it takes the click only when nothing is (<see cref="SitePress"/>). Hover and click
    /// share it, as in the original.
    /// </summary>
    (Picked Kind, float T) PickAt(Vector2 p)
    {
        (Picked Kind, float T) best = (Picked.None, float.MaxValue);
        void Offer(Picked kind, float? t) { if (t is { } v && v < best.T) best = (kind, v); }
        if (QuickOrder(p) is { Do: not null } order) Offer(Picked.Character, PickWhereAt(p, c => c.Id == order.Target)?.T ?? 0);
        else if (PickWhereAt(p, _ => true) is { } any && session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == any.Id) is { } held && Verbs(held).Count > 0)
            Offer(Picked.Character, any.T);   // no quick order, but a held click opens its menu
        var item = PickGroundItemAt(p);
        Offer(Picked.GroundItem, item?.T);
        var container = PickContainerAt(p);
        Offer(Picked.Container, container?.T);
        Offer(Picked.Door, PickDoorAt(p)?.T);
        var bed = PickBedAt(p);
        Offer(Picked.Bed, bed?.T);
        var cage = PickCageAt(p);
        Offer(Picked.Cage, cage?.T);
        Offer(Picked.Dummy, PickDummyAt(p)?.T);
        Offer(Picked.Rock, PickRockAt(p)?.T);
        if (PickSiteAt(p) is { } site && SiteIsObject(site.Site)) Offer(Picked.Site, site.T);
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

    /// <summary>Where a ground item is drawn (<see cref="GroundItemShape"/>), else where it lies; null when it is gone.</summary>
    Vector3? ItemAt(int id)
    {
        foreach (var g in session.CurrentSnapshot.GroundItems)
            if (g.Id == id) return GroundItemShape?.Invoke(g)?.At ?? g.Position;
        return null;
    }

    /// <summary>Whether a point lies inside the box of a building's collision placed at <paramref name="position"/> turned by <paramref name="yaw"/> (its footprint, from its lowest to its highest point).</summary>
    bool Holds(string record, Vector3 position, float yaw, Vector3 point) =>
        buildHost?.Placement.Footprint(record) is { } f && point.Y >= position.Y + f.YMin && point.Y <= position.Y + f.YMax
        && f.Gap(position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw), new Vector2(point.X, point.Z)) == 0;

    /// <summary>The host's test for where a ray enters a building seen from outside (its interior not shown), within a distance; null when none is in the way.</summary>
    public Func<Vector3, Vector3, float, float?>? ClosedBuildingHit { get; set; }

    /// <summary>The host's test for where a ray meets a shown floor or wall of a building in the interior view, within a distance; null when none is in the way.</summary>
    public Func<Vector3, Vector3, float, float?>? ShownInteriorHit { get; set; }

    /// <summary>
    /// Whether a building seen from outside, or a shown floor or wall of one in the interior view, stands on the ray before <paramref name="distance"/>: what lies inside, behind or below
    /// it is not pointed at.
    /// </summary>
    bool Hidden((Vector3 Origin, Vector3 Direction) ray, float distance) =>
        ClosedBuildingHit?.Invoke(ray.Origin, ray.Direction, distance) is not null || ShownInteriorHit?.Invoke(ray.Origin, ray.Direction, distance) is not null;

    IReadOnlyList<CharacterId> InBox(Vector2 a, Vector2 b)
    {
        float x0 = MathF.Min(a.X, b.X), x1 = MathF.Max(a.X, b.X), y0 = MathF.Min(a.Y, b.Y), y1 = MathF.Max(a.Y, b.Y);
        var list = new List<CharacterId>();
        foreach (var c in session.CurrentSnapshot.Characters)
        {
            if (!c.IsPlayer) continue;
            var p = where(c) + new Vector3(0, CharacterHeight / 2, 0);
            if (Vector3.Distance(Eye, p) > BoxSelectRange) continue;
            if (Project(p) is { } s && s.X >= x0 && s.X <= x1 && s.Y >= y0 && s.Y <= y1) list.Add(c.Id);
        }
        return list;
    }
}
