using System.Numerics;
using Meitou.Game.Building;
using Meitou.Simulation;
using Meitou.Simulation.Navigation;

namespace Meitou.Game.Ui;

/// <summary>
/// Picking: the ray through a pixel, its floor or terrain destination, which character it touches, the box select.
/// </summary>
sealed class WorldPicking(PlayerInterface player)
{
    #region Host hooks

    /// <summary>
    /// The host's building-floor pick, limited to a distance (<see cref="FloorReach"/>).
    /// </summary>
    public Func<Vector3, Vector3, float, Vector3?>? BuildingMoveHit { get; set; }

    /// <summary>
    /// The host's reach past a terrain hit (origin, direction, the hit) that lies inside a building's rooms, where the terrain is cut; null outside
    /// them.
    /// </summary>
    public Func<Vector3, Vector3, Vector3, float?>? CutGroundReach { get; set; }

    /// <summary>
    /// The host's pick of a raised walkable surface outside the rooms (a wall top, a ramp, a deck), limited to a distance.
    /// </summary>
    public Func<Vector3, Vector3, float, Vector3?>? SurfaceMoveHit { get; set; }

    /// <summary>
    /// The host's test for where a ray first meets the collision of a building with rooms on a face nobody stands on (a foundation's side), within
    /// a distance; null when none.
    /// </summary>
    public Func<Vector3, Vector3, float, float?>? InteriorFaceHit { get; set; }

    /// <summary>
    /// The host's test for a level building (by placement key) having rooms: its collision is <see cref="InteriorFaceHit"/>'s to answer.
    /// </summary>
    public Func<string, bool>? HasRooms { get; set; }

    /// <summary>
    /// The host's height of the highest walkable surface at a point's (x, z) that is not above it; null when there is none.
    /// </summary>
    public Func<Vector3, float?>? SurfaceBelow { get; set; }

    /// <summary>
    /// Debug: called with the ground point under the cursor (null when none) on a left press that no widget took; true consumes the press (the host's
    /// "Explode on click").
    /// </summary>
    public Func<Vector3?, bool>? WorldClick { get; set; }

    /// <summary>
    /// The renderer's test of a ray against a character's posed mesh (<c>CharacterRenderer.RayHit</c>): the distance, +infinity for a miss, null when
    /// it does not draw that character (the capsule decides then). Null without a renderer.
    /// </summary>
    public Func<CharacterId, Vector3, Vector3, float?>? MeshHit { get; set; }

    /// <summary>
    /// The host's test for where a ray enters a building seen from outside (its interior not shown), within a distance; null when none is in the way.
    /// </summary>
    public Func<Vector3, Vector3, float, float?>? ClosedBuildingHit { get; set; }

    /// <summary>
    /// The host's test for where a ray meets a shown floor or wall of a building in the interior view, within a distance; null when none is in the
    /// way.
    /// </summary>
    public Func<Vector3, Vector3, float, float?>? ShownInteriorHit { get; set; }

    #endregion

    #region Ray and ground

    /// <summary>
    /// How much nearer than the destination a steep face has to be met to end the ray, world units (engine choice).
    /// </summary>
    const float FaceMargin = 1;

    /// <summary>
    /// The open amount from which a door's leaf no longer ends a move click's ray: half open (engine choice).
    /// </summary>
    const float DoorOpenPast = 0.5f;

    /// <summary>
    /// The slope past which a collision face is no ground: the navigation slope limit, as the floor pick's.
    /// </summary>
    static readonly float SteepCos = MathF.Cos(40 * MathF.PI / 180);

    /// <summary>
    /// The movement destination on a raised walkable surface or a building floor, or the terrain when neither is in front of it. A steep face of
    /// a building met before it (a foundation's side, a wall) ends the ray: the destination is the foot of that face on the camera's side
    /// (docs/game/ui-input.md "Movement marker").
    /// </summary>
    public Vector3? MoveHit(Vector3 origin, Vector3 direction)
    {
        var terrain = GroundHit(origin, direction);
        float distance = terrain is { } hit ? Vector3.Distance(origin, hit) : 40000;
        var room = BuildingMoveHit?.Invoke(origin, direction, FloorReach(origin, direction, terrain));
        if (room is { } floor) distance = Vector3.Distance(origin, floor);

        var surface = SurfaceMoveHit?.Invoke(origin, direction, distance);
        var pick = surface ?? room ?? terrain;
        if (surface is { } raised) distance = Vector3.Distance(origin, raised);
        if (FaceHit(origin, direction, distance - FaceMargin) is not { } face) return pick;

        var at = origin + direction * face;
        if (FootOf(at, direction) is not { } foot) return pick;

        return surface is { } top && TopOf(top, at, foot) ? pick : foot;
    }

    /// <summary>
    /// How far along the ray a building's floor is looked for: up to the <paramref name="terrain"/> hit, or on through the room it lies in when the
    /// terrain is cut there (<see cref="CutGroundReach"/>); the world's reach when the ray meets no terrain.
    /// </summary>
    public float FloorReach(Vector3 origin, Vector3 direction, Vector3? terrain)
    {
        if (terrain is not { } hit) return 40000;

        float distance = Vector3.Distance(origin, hit);
        return CutGroundReach?.Invoke(origin, direction, hit) is { } reach ? MathF.Max(distance, reach) : distance;
    }

    /// <summary>
    /// How far along the ray a building pick looks, with the distance of the terrain hit (null: none, the reach is <paramref name="open"/>): up to
    /// the terrain, or on through the room it lies in when the terrain is cut there (<see cref="FloorReach"/>), as the original's walk goes on past
    /// a terrain hit inside a building (docs/game/ui-input.md "Building picking and selection"; a bench in a house dug into a hillside, #1049).
    /// </summary>
    internal (float Reach, float? Terrain) PickReach(Vector3 origin, Vector3 direction, float open = 6000)
    {
        var (reach, terrain) = SharedReach(origin, direction);
        return terrain is null ? (open, null) : (reach, terrain);
    }

    /// <summary>
    /// The distance within <paramref name="limit"/> at which the ray first meets a steep building face: of a building with rooms
    /// (<see cref="InteriorFaceHit"/>) or of one without (<see cref="StaticFaceHit"/>).
    /// </summary>
    float? FaceHit(Vector3 origin, Vector3 direction, float limit)
    {
        if (limit <= 0) return null;

        var rooms = InteriorFaceHit?.Invoke(origin, direction, limit);
        return StaticFaceHit(origin, direction, rooms ?? limit) ?? rooms;
    }

    /// <summary>
    /// The distance within <paramref name="limit"/> at which the ray first meets the collision of a building without rooms (a wall, a prop), a
    /// level one or one of the player's sites, when the triangle met is steep; null when the first one met can be stood on, or none is met.
    /// </summary>
    float? StaticFaceHit(Vector3 origin, Vector3 direction, float limit)
    {
        if (player.Build.Services is not { } host) return null;

        var nearest = (T: limit, Steep: false);
        foreach (var hit in StaticHits(host, origin, direction, limit))
        {
            if (hit.T < nearest.T) nearest = (hit.T, MathF.Abs(hit.Normal.Y) < SteepCos);
        }

        return nearest.Steep ? nearest.T : null;
    }

    /// <summary>
    /// The first hit of the ray within <paramref name="limit"/> on the collision of each building without rooms it meets: the level ones
    /// (<see cref="LevelHits"/>) and the player's sites (<see cref="SiteHits"/>).
    /// </summary>
    IEnumerable<StaticHit> StaticHits(BuildServices host, Vector3 origin, Vector3 direction, float limit) =>
        LevelHits(host, origin, direction, limit).Concat(SiteHits(host, origin, direction, limit));

    /// <summary>
    /// The first hit of the ray within <paramref name="limit"/> on the collision of each level building without rooms it meets.
    /// </summary>
    IEnumerable<StaticHit> LevelHits(BuildServices host, Vector3 origin, Vector3 direction, float limit)
    {
        foreach (var candidate in host.Placement.StaticAlong(origin, direction, limit))
        {
            var b = candidate.Placement;
            // A building with rooms answers for its own collision (InteriorFaceHit): its hidden floors do not count, its walls drop to the floor
            // inside, and a click on a closed house's wall goes in.
            if (candidate.Plot || HasRooms?.Invoke(b.Key) == true || player.LevelEditorPanel.Services?.IsCleared(b.Key) == true) continue;

            var mesh = ClosedMeshOf(b.BuildingId, b.Key, host.Placement.LevelDestroyed(b));
            float y = b.WorldY ?? player.Ground(b.Position.X, b.Position.Z) + b.Position.Y;
            var hull = candidate.Footprint.Place(b.Position, b.Rotation);
            if (mesh.Count == 0 || BuildMode.RayPrism(origin, direction, hull, y + candidate.Footprint.YMin, y + candidate.Footprint.YMax) is null)
                continue;

            var at = new Vector3(b.Position.X, y, b.Position.Z);
            if (RayMeshHit((origin, direction), mesh, at, b.Rotation) is { } hit && hit.T < limit)
                yield return new StaticHit(hit.T, hit.Normal, candidate.Footprint, at, b.Rotation);
        }
    }

    /// <summary>
    /// The first hit of the ray within <paramref name="limit"/> on the collision of each of the player's sites without rooms it meets (a wall, a
    /// gate, a machine): placed as <see cref="BuildMode.PickSiteAt"/> places it.
    /// </summary>
    IEnumerable<StaticHit> SiteHits(BuildServices host, Vector3 origin, Vector3 direction, float limit)
    {
        foreach (var site in player.Session.CurrentSnapshot.Buildings)
        {
            // Not drawn above the viewed floor of an opened house: no face there.
            if (HasRooms?.Invoke(site.Id) == true || player.Furniture.AboveViewedFloor?.Invoke(site.Position) == true) continue;
            if (host.Placement.Footprint(site.Record) is not { } footprint) continue;

            var mesh = ClosedMeshOf(site.Record, site.Id, site.Destroyed);
            var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, PlacementCheck.YawOf(site.Rotation));
            var hull = footprint.Place(site.Position, yaw);
            float y = site.Position.Y;
            bool tilted = Tilted(site.Rotation);
            if (mesh.Count == 0 || !tilted && BuildMode.RayPrism(origin, direction, hull, y + footprint.YMin, y + footprint.YMax) is null) continue;

            // A tilted site's collision stands with its whole turn (the original's shapes hang on the building's node).
            if (RayMeshHit((origin, direction), mesh, site.Position, tilted ? site.Rotation : yaw) is { } hit && hit.T < limit)
                yield return new StaticHit(hit.T, hit.Normal, footprint, site.Position, yaw);
        }
    }

    /// <summary>
    /// The collision triangles of building record <paramref name="record"/> placed as <paramref name="placement"/>, without the parts of its doors
    /// that are open (an open gate's leaves): a click through an open gateway goes to the ground beyond it. A <paramref name="destroyed"/> one's
    /// are its ruin's, which has no doors at all (the Hub's ruined gates).
    /// </summary>
    IReadOnlyList<(Vector3[] Vertices, int[] Indices)> ClosedMeshOf(string record, string placement, bool destroyed)
    {
        var mesh = CollisionMeshOf(record, destroyed);
        var doors = CollisionDoorsOf(record, destroyed);
        if (doors.Count != mesh.Count || !doors.Any(d => d >= 0)) return mesh;

        var open = player.Session.CurrentSnapshot.Doors;
        bool IsOpen(int door) => door >= 0 && open.TryGetValue((placement, door), out var view) && view.Open >= DoorOpenPast;
        if (!doors.Any(IsOpen)) return mesh;

        return [.. mesh.Where((_, i) => !IsOpen(doors[i]))];
    }

    /// <summary>
    /// Where a character stands at the foot of a face met at <paramref name="at"/>: a little back towards the camera, on the highest walkable
    /// surface not above the hit; null when there is none there (a wall standing in water: the seabed is no foot). The steps back are engine
    /// choices: the navmesh keeps clear of a wall.
    /// </summary>
    Vector3? FootOf(Vector3 at, Vector3 direction)
    {
        var flat = new Vector3(direction.X, 0, direction.Z);
        var back = flat.LengthSquared() < 1e-9f ? Vector3.Zero : -Vector3.Normalize(flat);

        foreach (float step in (ReadOnlySpan<float>)[1, 3, 6])
        {
            var p = at + back * step;
            if (SurfaceBelow?.Invoke(p) is { } y) return new Vector3(p.X, y, p.Z);
        }

        return null;
    }

    /// <summary>
    /// Whether the raised surface picked at <paramref name="top"/> is the top of the face met at <paramref name="at"/> (a wall walk behind its
    /// parapet): within 4 units of the hit (a wall's thickness), not above it, and more than a step (5) over the <paramref name="foot"/>. A surface level with the foot is the
    /// ground beyond a wall, which the face hides.
    /// </summary>
    static bool TopOf(Vector3 top, Vector3 at, Vector3 foot) =>
        Vector2.Distance(new(top.X, top.Z), new(at.X, at.Z)) <= 4
        && top.Y <= at.Y + 0.5f
        && top.Y > foot.Y + 5;

    /// <summary>
    /// Where the ray meets the ground: marched in growing steps, then bisected. Null when it leaves the world first.
    /// </summary>
    public Vector3? GroundHit(Vector3 origin, Vector3 direction, float maxDistance = 40000)
    {
        if (AboveGround(origin, direction, 0) <= 0) return origin;

        float previous = 0;
        float step = 2;
        for (float t = step; t <= maxDistance; t += step)
        {
            if (AboveGround(origin, direction, t) <= 0) return GroundBetween(origin, direction, previous, t);

            previous = t;
            step = MathF.Max(2, t * 0.01f);
        }

        return null;
    }

    /// <summary>
    /// How far the point at <paramref name="t"/> along the ray is above the ground.
    /// </summary>
    float AboveGround(Vector3 origin, Vector3 direction, float t)
    {
        var p = origin + direction * t;
        return p.Y - player.Ground(p.X, p.Z);
    }

    /// <summary>
    /// The ground point between two distances along the ray, by bisection.
    /// </summary>
    Vector3 GroundBetween(Vector3 origin, Vector3 direction, float lo, float hi)
    {
        for (int i = 0; i < 24; i++)
        {
            float mid = (lo + hi) / 2;
            if (AboveGround(origin, direction, mid) > 0)
                lo = mid;
            else
                hi = mid;
        }

        var p = origin + direction * hi;
        return new Vector3(p.X, player.Ground(p.X, p.Z), p.Z);
    }

    /// <summary>
    /// Whether the ray from its origin up to <paramref name="limit"/> along it passes within <paramref name="reach"/> of <paramref name="position"/>
    /// in X/Z, a unit wider for rounding far from the origin: a hit before the limit on anything within that X/Z reach lies on that part of the ray,
    /// whatever its height (the coarse cull of the picks).
    /// </summary>
    internal static bool RayPassesXz((Vector3 Origin, Vector3 Direction) ray, Vector3 position, float reach, float limit = float.MaxValue)
    {
        var to = new Vector2(position.X - ray.Origin.X, position.Z - ray.Origin.Z);
        var along = new Vector2(ray.Direction.X, ray.Direction.Z);
        float lengthSquared = along.LengthSquared();

        // The nearest point to the position of the ray's part up to limit (the origin for a ray straight up or down).
        float s = lengthSquared < 1e-12f ? 0 : Math.Clamp(Vector2.Dot(to, along) / lengthSquared, 0, MathF.Max(limit, 0));
        float widened = reach + 1;
        return (to - along * s).LengthSquared() <= widened * widened;
    }

    /// <summary>
    /// The distance along the ray at which it passes closest to the segment a-b, when within <paramref name="radius"/>.
    /// </summary>
    internal static float? RaySegment(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, float radius)
    {
        var u = direction;
        var v = b - a;
        var w = origin - a;
        float vv = Vector3.Dot(v, v);
        float uv = Vector3.Dot(u, v);
        float uw = Vector3.Dot(u, w);
        float vw = Vector3.Dot(v, w);
        float denominator = vv - uv * uv;   // |u| = 1
        float s = denominator < 1e-6f ? 0 : Math.Clamp((uv * vw - vv * uw) / denominator, 0, 1e9f);
        float q = vv < 1e-6f ? 0 : Math.Clamp((vw + s * uv) / vv, 0, 1);
        s = MathF.Max(0, uv * q - uw);
        var onRay = origin + u * s;
        var onSegment = a + v * q;
        return Vector3.Distance(onRay, onSegment) <= radius ? s : null;
    }

    #endregion

    #region Characters

    /// <summary>
    /// The player's character under the pixel (capsule from the feet up, a little fatter with distance), if any.
    /// </summary>
    public CharacterId? Pick(Vector2 pixel) => PickWhere(pixel, c => c.IsPlayer);

    /// <summary>
    /// A living character of anyone else's under the pixel: what a right click attacks (hostility is not checked yet).
    /// </summary>
    public CharacterId? PickTarget(Vector2 pixel) => PickWhere(pixel, c => !c.IsPlayer && c.Standing);

    /// <summary>
    /// A living trader under the pixel (a member of a squad with a shop): a right click trades with it, or attacks it when its faction is hostile
    /// (the world decides).
    /// </summary>
    public CharacterId? PickTrader(Vector2 pixel) => PickWhere(pixel, c => c.IsTrader && c.Standing);

    /// <summary>
    /// A knocked-out or dead character under the pixel that nobody carries (what a pick-up or a loot is for); <paramref name="ownOnly"/> takes only
    /// the player's.
    /// </summary>
    public CharacterId? PickDowned(Vector2 pixel, bool ownOnly = false) =>
        PickWhere(pixel, c => c.Body is { } b && (b.Unconscious || b.Dead) && c.CarriedBy.IsNone && (!ownOnly || c.IsPlayer))
        ?? (ownOnly ? null : PickWhere(pixel, c => !c.CarriedBy.IsNone));

    /// <summary>
    /// One of the player's own living characters under the pixel that first aid has something to bandage on (a right click on it orders the selection
    /// to treat it).
    /// </summary>
    public CharacterId? PickHurtAlly(Vector2 pixel) => PickWhere(pixel, c => c.IsPlayer && c.Body is { Dead: false, NeedsAid: true });

    /// <summary>
    /// The id of the nearest character under <paramref name="pixel"/> that <paramref name="accept"/> takes (<see cref="PickWhereAt"/>).
    /// </summary>
    internal CharacterId? PickWhere(Vector2 pixel, Func<CharacterSnapshot, bool> accept) => PickWhereAt(pixel, accept)?.Id;

    /// <summary>
    /// The nearest accepted character under the pixel with its ray distance; null when none, or when the nearest is hidden behind a building seen
    /// from outside.
    /// </summary>
    internal (CharacterId Id, float T)? PickWhereAt(Vector2 pixel, Func<CharacterSnapshot, bool> accept)
    {
        if (player.Ray(pixel) is not { } ray) return null;

        CharacterId? best = null;
        float bestT = float.MaxValue;
        foreach (var c in player.Session.CurrentSnapshot.Characters)
        {
            // not drawn: not picked, as the original's pick needs a visible character
            if (!accept(c) || player.StealthView.HiddenInWorld?.Invoke(c.Id) == true) continue;

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

        (CharacterId Id, float T)? picked = best is not { } found || player.Obscured(ray, bestT) ? null : (found, bestT);
        if (CageOccupant(pixel, accept, picked) is { } occupant) return occupant;

        return picked;
    }

    /// <summary>
    /// An occupied cage with occupant selection that the ray meets first gives its occupant (the original's FUN_1407fb410 for the cage's occupant).
    /// </summary>
    (CharacterId Id, float T)? CageOccupant(Vector2 pixel, Func<CharacterSnapshot, bool> accept, (CharacterId Id, float T)? picked)
    {
        var cage = player.Furniture.PickCageAt(pixel);
        if (cage is not { Cage: { OccupantSelection: true, Occupant: { IsNone: false } occupant }, T: var cageT }) return null;
        if (picked is { } nearer && !(cageT < nearer.T)) return null;
        if (player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == occupant) is not { } held) return null;
        if (!accept(held) || player.StealthView.HiddenInWorld?.Invoke(occupant) == true) return null;

        return (occupant, cageT);
    }

    #endregion

    #region Objects

    /// <summary>
    /// What a right click acts on, as <see cref="PickAt"/> chose it.
    /// </summary>
    internal enum Picked
    {
        /// <summary>
        /// Nothing.
        /// </summary>
        None,

        /// <summary>
        /// A character with a quick order.
        /// </summary>
        Character,

        /// <summary>
        /// An item on the ground.
        /// </summary>
        GroundItem,

        /// <summary>
        /// A container.
        /// </summary>
        Container,

        /// <summary>
        /// A door.
        /// </summary>
        Door,

        /// <summary>
        /// A rock to mine.
        /// </summary>
        Rock,

        /// <summary>
        /// A site that is an object.
        /// </summary>
        Site,

        /// <summary>
        /// A bed.
        /// </summary>
        Bed,

        /// <summary>
        /// A cage.
        /// </summary>
        Cage,

        /// <summary>
        /// A training dummy.
        /// </summary>
        Dummy,

        /// <summary>
        /// A town's turret.
        /// </summary>
        TownTurret,

        /// <summary>
        /// A seat.
        /// </summary>
        Seat,

        /// <summary>
        /// A town building that is a machine (a farm, a mine).
        /// </summary>
        TownMachine,
    }

    /// <summary>
    /// The right button's target under the pixel, with characters selected (docs/game/ui-input.md "Building picking and selection"): the nearest
    /// along the ray of a character with a quick order, a ground item, a container, a door, a rock, a town turret, a town machine (a farm by its plot)
    /// and a site that is an object (unfinished, a machine, storage, furnace or turret), as the original's pick walks its hits nearest first. What a building seen from outside hides is left out
    /// (each helper's <see cref="PlayerInterface.Hidden"/> test, the original's floor rule). A plain building is not a candidate: it takes the click
    /// only when nothing is (<see cref="BuildMode.SitePress"/>). Hover and click share it, as in the original.
    /// </summary>
    internal (Picked Kind, float T) PickAt(Vector2 p)
    {
        (Picked Kind, float T) best = (Picked.None, float.MaxValue);
        void Offer(Picked kind, float? t)
        {
            if (t is { } v && v < best.T) best = (kind, v);
        }

        if (player.QuickOrder(p) is { Do: not null } order)
        {
            Offer(Picked.Character, PickWhereAt(p, c => c.Id == order.Target)?.T ?? 0);
        }
        else if (PickWhereAt(p, _ => true) is { } any && player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == any.Id) is { } held
            && player.ContextMenuWindow.HasMenu(held))
        {
            Offer(Picked.Character, any.T);   // no quick order, but a held click opens its menu
        }

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
        Offer(Picked.TownTurret, player.Furniture.PickTownTurretAt(p)?.T);
        Offer(Picked.TownMachine, player.TownMachines.PickAt(p)?.T);
        if (player.Build.PickSiteAt(p) is { } site && player.Build.SiteIsObject(site.Site)) Offer(Picked.Site, site.T);

        // An item behind the container, bed or cage met first but lying inside its bounds takes the click (the original's walk, FUN_140800250: the
        // building's box holds the item).
        if (item is { } i && ItemAt(i.Id) is { } at && best.Kind switch
        {
            Picked.Container => Holds(container!.Value.Container.Record, container.Value.Container.Position, container.Value.Container.Yaw, at),
            Picked.Bed => Holds(bed!.Value.Bed.Record, bed.Value.Bed.Position, bed.Value.Bed.Yaw, at),
            Picked.Cage => Holds(cage!.Value.Cage.Record, cage.Value.Cage.Position, cage.Value.Cage.Yaw, at),
            _ => false,
        })
        {
            best = (Picked.GroundItem, i.T);
        }

        return best;
    }

    /// <summary>
    /// Where a ground item is drawn (<see cref="GroundItemPicks.GroundItemShape"/>), else where it lies; null when it is gone.
    /// </summary>
    Vector3? ItemAt(int id)
    {
        foreach (var g in player.Session.CurrentSnapshot.GroundItems)
        {
            if (g.Id == id) return player.GroundItemPicks.GroundItemShape?.Invoke(g)?.At ?? g.Position;
        }

        return null;
    }

    /// <summary>
    /// Whether a point lies inside the box of a building's collision placed at <paramref name="position"/> turned by <paramref name="yaw"/> (its
    /// footprint, from its lowest to its highest point).
    /// </summary>
    bool Holds(string record, Vector3 position, float yaw, Vector3 point) =>
        player.Build.Services?.Placement.Footprint(record) is { } f && point.Y >= position.Y + f.YMin && point.Y <= position.Y + f.YMax
        && f.Gap(position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw), new Vector2(point.X, point.Z)) == 0;

    /// <summary>
    /// The player's characters whose middle projects into the screen box from <paramref name="a"/> to <paramref name="b"/>, within the box-select
    /// range.
    /// </summary>
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

    #endregion

    #region Buildings

    /// <summary>
    /// Picking buildings against their collision triangles (docs/game/ui-input.md "Building picking and selection"): the original's pick is a raycast
    /// against the collision shapes, not a prism over their merged hull. The hull prism stays as the first, cheap test; a building that has collision
    /// files is then tested triangle by triangle, so ground, characters and neighbours next to a building are not taken for it.
    /// </summary>
    readonly Dictionary<(string Record, bool Destroyed), IReadOnlyList<(Vector3[] Vertices, int[] Indices)>> _collisionMeshes = [];

    /// <summary>
    /// Each shape of <see cref="_collisionMeshes"/>' meshes, by record and state: the index of the door it is a part of, -1 for none.
    /// </summary>
    readonly Dictionary<(string Record, bool Destroyed), IReadOnlyList<int>> _collisionDoors = [];

    /// <summary>
    /// The up axis's height below which a placement counts as tilted (<see cref="Tilted"/>): cos 0.8 degrees.
    /// </summary>
    const float TiltedUp = 0.9999f;

    /// <summary>
    /// The collision triangles of building record <paramref name="record"/>, intact or <paramref name="destroyed"/> (a ruin's: no doors), read once
    /// (empty without collision files).
    /// </summary>
    IReadOnlyList<(Vector3[] Vertices, int[] Indices)> CollisionMeshOf(string record, bool destroyed)
    {
        if (_collisionMeshes.TryGetValue((record, destroyed), out var known)) return known;

        var mesh = player.Build.Services?.Placement.CollisionMesh(record, destroyed) ?? [];
        _collisionMeshes[(record, destroyed)] = mesh;
        return mesh;
    }

    /// <summary>
    /// The door index of each of <see cref="CollisionMeshOf"/>'s shapes for building record <paramref name="record"/>, read once.
    /// </summary>
    IReadOnlyList<int> CollisionDoorsOf(string record, bool destroyed)
    {
        if (_collisionDoors.TryGetValue((record, destroyed), out var known)) return known;

        var doors = player.Build.Services?.Placement.CollisionMeshDoors(record, destroyed) ?? [];
        _collisionDoors[(record, destroyed)] = doors;
        return doors;
    }

    /// <summary>
    /// Whether a placement's turn tilts it away from upright (more than about 0.8 degrees), so its upright footprint prism no longer holds its
    /// collision.
    /// </summary>
    internal static bool Tilted(Quaternion rotation) =>
        rotation.LengthSquared() > 1e-12f && Vector3.Transform(Vector3.UnitY, Quaternion.Normalize(rotation)).Y < TiltedUp;

    /// <summary>
    /// Where the ray meets building record <paramref name="record"/>'s collision placed at <paramref name="position"/> with its whole
    /// <paramref name="rotation"/>; null when it has none or the ray misses it.
    /// </summary>
    internal float? TiltedBuildingRayT((Vector3 Origin, Vector3 Direction) ray, string record, Vector3 position, Quaternion rotation)
    {
        var mesh = CollisionMeshOf(record, destroyed: false);
        return mesh.Count == 0 ? null : RayMesh(ray, mesh, position, rotation);
    }

    /// <summary>
    /// Where the ray meets a building: its hull prism first (null: no hit), then, when the record has collision triangles, the nearest of those
    /// (null: the ray passes through the prism without touching the building). <paramref name="y"/> is the building's base height.
    /// </summary>
    internal float? BuildingRayT((Vector3 Origin, Vector3 Direction) ray, Vector2[] hull, float yMin, float yMax, string? record, Vector3 position,
        float y, Quaternion rotation)
    {
        if (BuildMode.RayPrism(ray.Origin, ray.Direction, hull, y + yMin, y + yMax) is not { } prism) return null;
        if (record is null) return prism;

        var mesh = CollisionMeshOf(record, destroyed: false);
        return mesh.Count == 0 ? prism : RayMesh(ray, mesh, new Vector3(position.X, y, position.Z), rotation);
    }

    /// <summary>
    /// Where the ray meets a building without collision (a farm) by its plot (<see cref="Building.PlacementServices.Plot"/>, an engine stand-in):
    /// just before the ground, when the ground the ray meets at <paramref name="ground"/> lies inside the placed hull; null otherwise.
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

    /// <summary>
    /// The nearest hit of the ray on the triangles (double sided), the mesh turned by <paramref name="rotation"/> and put at <paramref name="at"/>;
    /// null for none.
    /// </summary>
    internal static float? RayMesh((Vector3 Origin, Vector3 Direction) ray, IReadOnlyList<(Vector3[] Vertices, int[] Indices)> mesh, Vector3 at,
        Quaternion rotation) => RayMeshHit(ray, mesh, at, rotation)?.T;

    /// <summary>
    /// <see cref="RayMesh"/> with the unit normal of the triangle met, in world space (either way round: the winding is not known).
    /// </summary>
    internal static (float T, Vector3 Normal)? RayMeshHit((Vector3 Origin, Vector3 Direction) ray,
        IReadOnlyList<(Vector3[] Vertices, int[] Indices)> mesh, Vector3 at, Quaternion rotation)
    {
        var q = rotation.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(rotation);
        var inverse = Quaternion.Conjugate(q);
        var origin = Vector3.Transform(ray.Origin - at, inverse);
        var direction = Vector3.Transform(ray.Direction, inverse);
        float? best = null;
        var normal = Vector3.UnitY;
        foreach (var (vertices, indices) in mesh)
        {
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
                if (distance <= 0 || best is { } nearer && distance >= nearer) continue;

                best = distance;
                normal = Vector3.Cross(e1, e2);
            }
        }

        if (best is not { } met) return null;

        return (met, Vector3.Transform(normal.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(normal), q));
    }

    #endregion

    #region Occlusion

    /// <summary>
    /// How much nearer than a pick's hit a blocking hit has to be to hide it, world units: a building's own collision, met where its pick meets it,
    /// hides nothing (engine choice).
    /// </summary>
    const float BlockMargin = 1;

    /// <summary>
    /// The blockers of the shared pick's ray: its scope, ray and <see cref="Blockers"/>'s answer.
    /// </summary>
    (int Scope, Vector3 Origin, Vector3 Direction, float Ground, List<StaticHit> Hits) _blockers;

    /// <summary>
    /// A hit of a ray on the collision of a building without rooms, with the building's footprint and placement.
    /// </summary>
    readonly record struct StaticHit(float T, Vector3 Normal, BuildingFootprint Footprint, Vector3 Position, Quaternion Rotation)
    {
        /// <summary>
        /// Whether <paramref name="point"/> lies inside one of the building's collision shapes (its hull on the ground, from the building's
        /// lowest to its highest point): a body in a bed or a cage, an item in a container.
        /// </summary>
        public bool Holds(Vector3 point)
        {
            if (point.Y < Position.Y + Footprint.YMin || point.Y > Position.Y + Footprint.YMax) return false;

            var xz = new Vector2(point.X, point.Z);
            var shapes = Footprint.Shapes.Count > 0 ? Footprint.Shapes : [Footprint.Hull];
            foreach (var shape in shapes)
            {
                if (new BuildingFootprint(shape, Footprint.YMin, Footprint.YMax).Gap(Position, Rotation, xz) == 0) return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Whether the terrain or the collision of a building without rooms stands on the ray before a character's or an object's hit at
    /// <paramref name="distance"/>, as the original's pick walks its hits nearest first and ends at the ground or at a building taken first
    /// (docs/game/ui-input.md "Building picking and selection"). A building whose collision shape holds the hit point (a bed, a cage, a container)
    /// does not hide it.
    /// </summary>
    internal bool Blocked((Vector3 Origin, Vector3 Direction) ray, float distance)
    {
        var (ground, hits) = Blockers(ray.Origin, ray.Direction);
        float before = distance - BlockMargin;
        if (ground < before) return true;

        var at = ray.Origin + ray.Direction * distance;
        foreach (var hit in hits)
        {
            if (hit.T < before && !hit.Holds(at)) return true;
        }

        return false;
    }

    /// <summary>
    /// How far the ray reaches before the ground (<see cref="PickReach"/>; the world's reach when it meets none) and the hits on buildings without
    /// rooms up to there, kept for the shared pick's same ray.
    /// </summary>
    (float Ground, List<StaticHit> Hits) Blockers(Vector3 origin, Vector3 direction)
    {
        if (SharedScope != 0 && _blockers.Scope == SharedScope && _blockers.Origin == origin && _blockers.Direction == direction)
            return (_blockers.Ground, _blockers.Hits);

        float ground = PickReach(origin, direction, float.MaxValue).Reach;
        float limit = MathF.Min(ground, 40000);
        List<StaticHit> hits = player.Build.Services is { } host ? [.. StaticHits(host, origin, direction, limit)] : [];
        if (SharedScope != 0) _blockers = (SharedScope, origin, direction, ground, hits);
        return (ground, hits);
    }

    #endregion

    #region Shared pick

    /// <summary>
    /// The shared pick open (<see cref="BeginShared"/>), else 0; a new number for each.
    /// </summary>
    public int SharedScope { get; private set; }

    /// <summary>
    /// The last scope's number, so each <see cref="BeginShared"/> gets a new one.
    /// </summary>
    int _lastScope;

    /// <summary>
    /// The ground reach of the shared pick: its scope, ray and <see cref="SharedReach"/>'s answer.
    /// </summary>
    (int Scope, Vector3 Origin, Vector3 Direction, float Reach, float? Terrain) _reach;

    /// <summary>
    /// Opens a shared pick (the pointer's): until <see cref="EndShared"/> nothing the picks read changes, so the ground reach and the site pick are
    /// worked out once and reused by every pick in it.
    /// </summary>
    public void BeginShared() => SharedScope = ++_lastScope;

    /// <summary>
    /// Closes the shared pick: picks work everything out again.
    /// </summary>
    public void EndShared() => SharedScope = 0;

    /// <summary>
    /// The ray's floor reach and terrain distance (null: it meets no terrain, the reach then meaningless), kept for the shared pick's same ray.
    /// </summary>
    (float Reach, float? Terrain) SharedReach(Vector3 origin, Vector3 direction)
    {
        if (SharedScope != 0 && _reach.Scope == SharedScope && _reach.Origin == origin && _reach.Direction == direction)
            return (_reach.Reach, _reach.Terrain);

        var hit = GroundHit(origin, direction);
        (float, float?) found = hit is { } h ? (FloorReach(origin, direction, h), Vector3.Distance(origin, h)) : (0, null);
        if (SharedScope != 0) _reach = (SharedScope, origin, direction, found.Item1, found.Item2);
        return found;
    }

    #endregion
}
