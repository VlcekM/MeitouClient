using System.Numerics;
using Meitou.Data.Gameplay.Construction;

namespace Meitou.Game.Building;

/// <summary>
/// One piece of a wall line: a BUILDING (the wall, a short piece, a connector or a wedge) standing at its start point, facing along its own +Z.
/// </summary>
public readonly record struct WallPiece(BuildingBuildData Data, Vector3 Position, Quaternion Rotation);

/// <summary>
/// A link point a line can snap to: where it is, the wall's direction there (horizontal) and the original's code (1 free end, 2 free start, 3 joined
/// start or end).
/// </summary>
public readonly record struct WallSnap(Vector3 Point, Vector3 Direction, int Code)
{
    /// <summary>
    /// A line that starts on another wall's start runs backwards (<b>Verified</b>: reversed = code &amp; 2).
    /// </summary>
    public bool Reverses => (Code & 2) != 0;
}

/// <summary>
/// A placed or queued wall's two link points with their directions and how many other walls touch each.
/// </summary>
public sealed class WallLink(Vector3 start, Vector3 end, Vector3 startDirection, Vector3 endDirection)
{
    /// <summary>
    /// The wall's start link point.
    /// </summary>
    public Vector3 Start { get; } = start;
    /// <summary>
    /// The wall's end link point.
    /// </summary>
    public Vector3 End { get; } = end;
    /// <summary>
    /// The wall's horizontal direction at its start.
    /// </summary>
    public Vector3 StartDirection { get; } = startDirection;
    /// <summary>
    /// The wall's horizontal direction at its end.
    /// </summary>
    public Vector3 EndDirection { get; } = endDirection;
    /// <summary>
    /// How many other walls' link points join this one's start (within <see cref="WallLine.JoinDistanceSquared"/>).
    /// </summary>
    public int JoinsAtStart { get; set; }
    /// <summary>
    /// How many other walls' link points join this one's end.
    /// </summary>
    public int JoinsAtEnd { get; set; }
}

/// <summary>
/// Kenshi's wall lines (docs/game/buildings-production.md "Walls", <c>PreviewBuilding_Wall</c>): the link points of walls, the snap search, the bend
/// pieces at a snapped joint and the straight run that splits a line into the wall's pieces over the terrain. Pure over its inputs; the build mode in
/// <c>BuildMode</c> drives it.
/// </summary>
public static class WallLine
{
    #region Link points

    /// <summary>
    /// Snap radius outdoors (<b>Verified</b>; 25 inside a building, not built).
    /// </summary>
    public const float SnapRadius = 90;
    /// <summary>
    /// Two link points closer than the square root of this are joined (<b>Verified</b>).
    /// </summary>
    public const float JoinDistanceSquared = 10;
    /// <summary>
    /// A connector's end direction is turned this much about +Y (<b>Verified</b>).
    /// </summary>
    public const float ConnectorTurnDegrees = 48;
    /// <summary>
    /// A BUILDING's <c>link type</c> values (WallSectionLinkType): 1 connector, 2 lower wedge, 3 single (towers, gates; it takes no bend pieces).
    /// </summary>
    const int Connector = 1, Wedge = 2, Single = 3;

    /// <summary>
    /// A wall's start (its position) and end (position + rotation x (0, 0, link length)).
    /// </summary>
    public static (Vector3 Start, Vector3 End) Ends(BuildingBuildData wall, Vector3 position, Quaternion rotation) =>
        (position, position + Vector3.Transform(new Vector3(0, 0, wall.LinkLength), Normalised(rotation)));

    /// <summary>
    /// The link points of the walls (records with <c>links together</c>), with the join counts (<b>Verified</b>, <c>FUN_1404289f0</c>).
    /// </summary>
    public static List<WallLink> Links(IEnumerable<(BuildingBuildData Data, Vector3 Position, Quaternion Rotation)> walls)
    {
        var links = new List<WallLink>();
        foreach (var (data, position, rotation) in walls)
        {
            if (!data.LinksTogether) continue;
            var (start, end) = Ends(data, position, rotation);
            var dir = Horizontal(Vector3.Transform(Vector3.UnitZ, Normalised(rotation)));
            var endDir = dir;
            if (data.LinkType == Connector)
                endDir = Vector3.Transform(dir, Quaternion.CreateFromAxisAngle(Vector3.UnitY, ConnectorTurnDegrees * MathF.PI / 180));
            links.Add(new WallLink(start, end, dir, endDir));
        }
        for (int i = 0; i < links.Count; i++)
            for (int j = 0; j < links.Count; j++)
            {
                if (i == j) continue;
                var (a, b) = (links[i], links[j]);
                if (Vector3.DistanceSquared(a.Start, b.Start) < JoinDistanceSquared
                    || Vector3.DistanceSquared(a.Start, b.End) < JoinDistanceSquared)
                    a.JoinsAtStart++;
                if (Vector3.DistanceSquared(a.End, b.Start) < JoinDistanceSquared
                    || Vector3.DistanceSquared(a.End, b.End) < JoinDistanceSquared)
                    a.JoinsAtEnd++;
            }
        return links;
    }

    /// <summary>
    /// The nearest link point to <paramref name="at"/> in X/Z (<b>Verified</b>, <c>FUN_140427950</c>): a free point scores its distance squared minus
    /// the radius squared, a joined one its distance squared, below the radius squared; so free points snap within radius x sqrt 2 and win. The best
    /// one is kept only when its code shares a bit with <paramref name="mask"/>.
    /// </summary>
    public static WallSnap? Find(IReadOnlyList<WallLink> links, Vector3 at, int mask = 3, float radius = SnapRadius)
    {
        float r2 = radius * radius, best = r2;
        WallSnap? found = null;
        foreach (var link in links)
        {
            Consider(link.Start, link.StartDirection, link.JoinsAtStart == 0 ? 2 : 3, link.JoinsAtStart == 0);
            Consider(link.End, link.EndDirection, link.JoinsAtEnd == 0 ? 1 : 3, link.JoinsAtEnd == 0);
        }
        return found is { } f && (f.Code & mask) != 0 ? f : null;

        void Consider(Vector3 point, Vector3 dir, int code, bool free)
        {
            float dx = point.X - at.X, dz = point.Z - at.Z, d2 = dx * dx + dz * dz;
            float score = free ? d2 - r2 : d2;
            if (score < best) (best, found) = (score, new WallSnap(point, dir, code));
        }
    }

    #endregion

    #region Laying a line

    /// <summary>
    /// A line is at most this long (<b>Verified</b>).
    /// </summary>
    public const float MaxRun = 10000;
    /// <summary>
    /// A piece fits when its length is below what remains plus this (<b>Verified</b>), so the last may overshoot by up to it.
    /// </summary>
    public const float PieceTolerance = 10;
    /// <summary>
    /// The run stops when less than this remains (<b>Verified</b>).
    /// </summary>
    public const float StopDistance = 10;
    /// <summary>
    /// At most this many pieces in a run (<b>Verified</b>).
    /// </summary>
    public const int MaxPieces = 500;
    /// <summary>
    /// A joint bending more than this (degrees) gets a short piece over a crest or a wedge in a valley (<b>Verified</b>).
    /// </summary>
    public const float BendDegrees = 10;
    /// <summary>
    /// At a snapped joint one extra short piece per this many degrees of turn (<b>Verified</b>).
    /// </summary>
    public const float BendStepDegrees = 60;
    /// <summary>
    /// The constant a straight continuation compares the line's height over the ground with (<c>DAT_14167fad4</c>, <b>Verified</b> 10).
    /// </summary>
    const float Rise = 10;

    /// <summary>
    /// The pieces of a line from <paramref name="start"/> to <paramref name="end"/> (<b>Verified</b> unless the docs say otherwise,
    /// <c>FUN_14042af70</c>, <c>FUN_14042a4b0</c>, <c>FUN_140429cc0</c>): bend pieces at a snapped start and end, then the straight run between.
    /// Unsnapped ends carry the player's height offset already. The run is laid twice when the end is snapped, the second time with each piece
    /// shortened by the first run's overshoot over its count so it closes on the point (engine reading).
    /// </summary>
    public static List<WallPiece> Layout(BuildingBuildData master, Func<string, BuildingBuildData?> data, Func<float, float, float> ground,
        Vector3 start, WallSnap? startSnap, Vector3 end, WallSnap? endSnap, bool reversed)
    {
        var set = PieceSet.Of(master, data);
        var pieces = new List<WallPiece>();
        if (set.Pieces.Count == 0) return pieces;
        if (startSnap is { } s) start = Bend(set, pieces, start, s.Direction * (reversed ? -1 : 1), end, reversed);
        if (endSnap is { } e) end = Bend(set, pieces, end, e.Direction * (reversed ? 1 : -1), start, !reversed);
        int bends = pieces.Count;
        float overshoot = Run(set, pieces, ground, start, end, reversed, 0);
        if (endSnap is not null && overshoot != 0 && pieces.Count > bends)
        {
            float stretch = overshoot / (pieces.Count - bends);
            pieces.RemoveRange(bends, pieces.Count - bends);
            set.Reset();
            Run(set, pieces, ground, start, end, reversed, stretch);
        }
        return pieces;
    }

    /// <summary>
    /// The pieces a line may use (<b>Verified</b>): the wall and its subsections with a length, longest first; the connector and the wedge apart.
    /// </summary>
    sealed class PieceSet
    {
        /// <summary>
        /// The wall and its subsections with a link length, longest first (at most 64).
        /// </summary>
        public readonly List<BuildingBuildData> Pieces = [];
        /// <summary>
        /// Which of <see cref="Pieces"/> the current run has used, by index.
        /// </summary>
        public readonly bool[] Used = new bool[64];
        /// <summary>
        /// The wall's connector and wedge pieces; null when it has none.
        /// </summary>
        public BuildingBuildData? Connector, Wedge;
        /// <summary>
        /// The wall's <c>max slope</c> and <c>building height</c> (the wall object's +0x88 and +0x8c).
        /// </summary>
        public float MaxSlope, Height;
        /// <summary>
        /// The shortest piece.
        /// </summary>
        public BuildingBuildData Shortest => Pieces[^1];
        /// <summary>
        /// The longest piece.
        /// </summary>
        public BuildingBuildData Longest => Pieces[0];

        /// <summary>
        /// Clears the used marks for a new run.
        /// </summary>
        public void Reset() => Array.Clear(Used);

        /// <summary>
        /// The pieces of <paramref name="master"/> and its subsections, sorted longest first (equal lengths in data order).
        /// </summary>
        public static PieceSet Of(BuildingBuildData master, Func<string, BuildingBuildData?> data)
        {
            var set = new PieceSet { MaxSlope = master.MaxSlope, Height = master.BuildingHeight };
            foreach (var piece in master.WallSubsections.Select(data).Prepend(master))
            {
                if (piece is null) continue;
                if (piece.LinkType == WallLine.Connector) set.Connector = piece;
                else if (piece.LinkType == WallLine.Wedge) set.Wedge = piece;
                else if (piece.LinkLength > 0 && set.Pieces.Count < set.Used.Length) set.Pieces.Add(piece);
            }
            // Stable: equal lengths keep the data order.
            var sorted = set.Pieces.Select((p, i) => (p, i)).OrderByDescending(x => x.p.LinkLength).ThenBy(x => x.i).Select(x => x.p).ToList();
            set.Pieces.Clear();
            set.Pieces.AddRange(sorted);
            return set;
        }
    }

    /// <summary>
    /// The pieces at a snapped joint (<c>FUN_140429cc0</c>): the turn from the snap direction to the line, one short piece per
    /// <see cref="BendStepDegrees"/> (none when the shortest piece is a single), each turned an equal share; a connector at each joint on one side of
    /// the turn. Returns where the line goes on from.
    /// </summary>
    static Vector3 Bend(PieceSet set, List<WallPiece> pieces, Vector3 point, Vector3 direction, Vector3 other, bool reversed)
    {
        var a = Horizontal(direction);
        var b = Horizontal(other - point);
        if (a == Vector3.Zero || b == Vector3.Zero) return point;
        var perp = new Vector3(a.Z, 0, -a.X);
        bool conn = (Vector3.Dot(perp, b) > -0.01f) ^ reversed;
        float angle = MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1, 1)) * 180 / MathF.PI;
        int n = set.Shortest.LinkType == Single ? 0 : (int)(angle / BendStepDegrees);
        float sign = Vector3.Cross(a, b).Y >= 0 ? 1 : -1;
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, sign * angle / (n + 1) * MathF.PI / 180);
        float length = set.Shortest.LinkLength;
        var c = point;
        var w = a * 100;
        if (conn && set.Connector is { } first) pieces.Add(Facing(first, c, reversed ? c - w : c + w));
        for (int i = 0; i < n; i++)
        {
            w = Vector3.Transform(w, turn);
            var next = c + Vector3.Normalize(w) * length;
            pieces.Add(reversed ? Facing(set.Shortest, next, c) : Facing(set.Shortest, c, next));
            c = next;
            if (conn && set.Connector is { } connector) pieces.Add(Facing(connector, c, reversed ? c - w : c + w));
        }
        return c;
    }

    /// <summary>
    /// The straight run (<c>FUN_14042a4b0</c>): a terrain profile at half the longest piece, smoothed downward; the first piece shorter than what
    /// remains plus <see cref="PieceTolerance"/> ends on the profile (the height offset blended from the start's to the end's), a short piece over
    /// each crest and a wedge in each valley above <see cref="BendDegrees"/>. Returns the signed overshoot past the end along the line.
    /// </summary>
    static float Run(PieceSet set, List<WallPiece> pieces, Func<float, float, float> ground, Vector3 start, Vector3 end, bool reversed, float stretch)
    {
        var flat = new Vector3(end.X - start.X, 0, end.Z - start.Z);
        float total = flat.Length();
        if (total < 1e-3f) return 0;
        var dir = flat / total;
        total = MathF.Min(total, MaxRun);
        end = new Vector3(start.X + dir.X * total, end.Y, start.Z + dir.Z * total);

        var (smoothed, along) = Profile(set, ground, start, dir, total);
        float startOffset = start.Y - smoothed[0].Y, endOffset = end.Y - At(along, smoothed, total);
        var cur = start;
        float rem = total;
        var before = smoothed[1] - smoothed[0];
        for (int count = 0; ;)
        {
            int k = NextPiece(set, rem);
            if (k < 0) break;
            var piece = set.Pieces[k];
            float len = piece.LinkLength;
            var flatAt = new Vector3(cur.X + dir.X * len, 0, cur.Z + dir.Z * len);
            float s = Vector3.Dot(new Vector3(flatAt.X - start.X, 0, flatAt.Z - start.Z), dir);
            int seg = Segment(along, s);
            float t = (s - along[seg]) / MathF.Max(along[seg + 1] - along[seg], 1e-6f);
            if (t > 2) break;
            var p = Vector3.Lerp(smoothed[seg], smoothed[seg + 1], t);
            p.Y += endOffset + (rem - len) / total * (startOffset - endOffset);
            var toward = p - cur;
            if (toward.LengthSquared() < 1e-9f) break;
            p = cur + Vector3.Normalize(toward) * (len - stretch);
            var d = p - cur;

            // A rising piece steeper than the wall's max slope is not placed: the run goes on straight (FUN_14042a4b0 -> FUN_14042a2b0) and ends.
            if (p.Y > cur.Y && Angle(dir, d) > set.MaxSlope)
            {
                var fromStart = new Vector3(cur.X - start.X, 0, cur.Z - start.Z);
                cur = Straight(set, pieces, smoothed, Segment(along, Vector3.Dot(fromStart, dir)), cur, before, reversed);
                break;
            }

            AddBend(set, pieces, cur, before, d, reversed);
            pieces.Add(reversed ? Facing(piece, p, cur) : Facing(piece, cur, p));
            if (piece.LinkType == Single) set.Used[k] = true;
            before = d;
            cur = p;
            rem = Vector3.Distance(end, cur);
            count++;
            if (rem < StopDistance || Vector3.Dot(new Vector3(end.X - cur.X, 0, end.Z - cur.Z), dir) < 0 || count > MaxPieces) break;
        }
        return Vector3.Dot(new Vector3(cur.X - end.X, 0, cur.Z - end.Z), dir);
    }

    /// <summary>
    /// The run's terrain profile: ground points a step (half the longest piece) apart along the slope from <paramref name="start"/>, smoothed,
    /// and each point's distance along <paramref name="dir"/>.
    /// </summary>
    static (Vector3[] Smoothed, List<float> Along) Profile(PieceSet set, Func<float, float, float> ground, Vector3 start, Vector3 dir, float total)
    {
        // The profile: ground points a step apart along the slope, then each lowered towards its neighbours' mean (never raised).
        float step = set.Longest.LinkLength * 0.5f;
        if (step <= 0) step = 10;
        var profile = new List<Vector3> { new(start.X, ground(start.X, start.Z), start.Z) };
        var along = new List<float> { 0 };
        while (along[^1] <= total && profile.Count < 4096)
        {
            var prev = profile[^1];
            var q = prev + dir * step;
            q.Y = ground(q.X, q.Z);
            var delta = q - prev;
            var p = prev + (delta.LengthSquared() > 1e-9f ? Vector3.Normalize(delta) : dir) * step;
            profile.Add(p);
            along.Add(Vector3.Dot(new Vector3(p.X - start.X, 0, p.Z - start.Z), dir));
        }
        var smoothed = profile.ToArray();
        for (int i = 1; i < profile.Count - 1; i++)
            smoothed[i].Y = MathF.Min(profile[i].Y, 0.25f * profile[i - 1].Y + 0.5f * profile[i].Y + 0.25f * profile[i + 1].Y);
        return (smoothed, along);
    }

    /// <summary>
    /// The first unused piece shorter than <paramref name="rem"/> plus <see cref="PieceTolerance"/>; -1 when none is.
    /// </summary>
    static int NextPiece(PieceSet set, float rem)
    {
        int k = -1;
        for (int i = 0; i < set.Pieces.Count; i++)
        {
            if (!set.Used[i] && set.Pieces[i].LinkLength < rem + PieceTolerance)
            {
                k = i;
                break;
            }
        }
        return k;
    }

    /// <summary>
    /// The piece at a bend of the run at <paramref name="cur"/>: a short piece over a crest, a wedge in a valley, none below
    /// <see cref="BendDegrees"/>.
    /// </summary>
    static void AddBend(PieceSet set, List<WallPiece> pieces, Vector3 cur, Vector3 before, Vector3 d, bool reversed)
    {
        float bend = Angle(before, d);
        if (Vector3.Normalize(d).Y - Vector3.Normalize(before).Y > 0) bend = -bend;
        if (bend > BendDegrees && set.Shortest.LinkType != Single)
        {
            var bisector = Vector3.Normalize(Vector3.Normalize(d) + Vector3.Normalize(before)) * (set.Shortest.LinkLength / 2);
            pieces.Add(reversed ? Facing(set.Shortest, cur + bisector, cur - bisector) : Facing(set.Shortest, cur - bisector, cur + bisector));
        }
        else if (bend < -BendDegrees && set.Wedge is { } wedge)
            pieces.Add(reversed ? Facing(wedge, cur, cur - d) : Facing(wedge, cur, cur + before));
    }

    /// <summary>
    /// The profile segment holding distance <paramref name="s"/> along the run (the last one past its end).
    /// </summary>
    static int Segment(List<float> along, float s)
    {
        for (int i = 0; i < along.Count - 2; i++)
            if (s <= along[i + 1]) return i;
        return along.Count - 2;
    }

    /// <summary>
    /// The smoothed profile's height at distance <paramref name="s"/> along the run.
    /// </summary>
    static float At(List<float> along, Vector3[] smoothed, float s)
    {
        int i = Segment(along, s);
        float t = Math.Clamp((s - along[i]) / MathF.Max(along[i + 1] - along[i], 1e-6f), 0, 1);
        return float.Lerp(smoothed[i].Y, smoothed[i + 1].Y, t);
    }

    /// <summary>
    /// The straight continuation past a slope that is too steep (<c>FUN_14042a2b0</c>, <b>Verified</b>): along the previous piece's direction, the
    /// profile points from <paramref name="from"/> on are scanned for how far the line stands above the ground; the scan stops at the first point
    /// where the ground rises <c>building height</c> - 10 above the line (<see cref="Rise"/>), else at the point of greatest rise. Pieces of the
    /// longest length follow the line for as long as half a piece is less than the distance left (a single only once). Returns where the last one
    /// ends.
    /// </summary>
    static Vector3 Straight(PieceSet set, List<WallPiece> pieces, Vector3[] profile, int from, Vector3 cur, Vector3 before, bool reversed)
    {
        if (before.LengthSquared() < 1e-9f) return cur;
        var line = Vector3.Normalize(before);
        float distance = 0, bestDistance = 0, lowest = 0;
        for (int i = from; i < profile.Length; i++)
        {
            distance = Vector3.Dot(line, profile[i] - cur);
            float above = line.Y * distance + cur.Y - profile[i].Y;
            if (above < lowest)
            {
                bestDistance = distance;
                lowest = above;
            }
            if (above < Rise - set.Height) break;
            distance = bestDistance;
        }
        var piece = set.Longest;
        float length = piece.LinkLength;
        while (length * 0.5f < distance)
        {
            var next = cur + line * length;
            pieces.Add(reversed ? Facing(piece, next, cur) : Facing(piece, cur, next));
            cur = next;
            if (piece.LinkType == Single) break;
            distance -= length;
        }
        return cur;
    }

    #endregion

    #region Geometry

    /// <summary>
    /// Near a snapped joint (pieces within <see cref="JointRun"/> of it) walls with a link point within <see cref="JointReach"/> of it do not block
    /// (<b>Observed</b>).
    /// </summary>
    public const float JointReach = 20, JointRun = 120;

    /// <summary>
    /// A piece at <paramref name="from"/> facing <paramref name="to"/> (<b>Verified</b>, <c>FUN_1404278b0</c>): +Z towards the target (pitching with
    /// the slope), +X level.
    /// </summary>
    public static WallPiece Facing(BuildingBuildData data, Vector3 from, Vector3 to) => new(data, from, LookRotation(to - from));

    /// <summary>
    /// The rotation whose +Z is <paramref name="forward"/> and whose +X is level (Y x Z).
    /// </summary>
    public static Quaternion LookRotation(Vector3 forward)
    {
        if (forward.LengthSquared() < 1e-9f) return Quaternion.Identity;
        var z = Vector3.Normalize(forward);
        var xRaw = Vector3.Cross(Vector3.UnitY, z);
        if (xRaw.LengthSquared() < 1e-9f) return Quaternion.Identity;
        var x = Vector3.Normalize(xRaw);
        var y = Vector3.Cross(z, x);
        var m = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    /// <summary>
    /// Whether a placed piece is within <see cref="JointRun"/> of a joint, and so may overlap walls whose link point is at that joint.
    /// </summary>
    public static bool NearJoint(WallPiece piece, Vector3 joint)
    {
        var (a, b) = Ends(piece.Data, piece.Position, piece.Rotation);
        var ab = new Vector2(b.X - a.X, b.Z - a.Z);
        var ap = new Vector2(joint.X - a.X, joint.Z - a.Z);
        float t = ab.LengthSquared() < 1e-9f ? 0 : Math.Clamp(Vector2.Dot(ap, ab) / ab.LengthSquared(), 0, 1);
        return Vector2.Distance(ap, ab * t) <= JointRun;
    }

    /// <summary>
    /// The angle between two vectors in degrees; 0 when either is near zero.
    /// </summary>
    static float Angle(Vector3 a, Vector3 b) =>
        a.LengthSquared() < 1e-9f || b.LengthSquared() < 1e-9f
            ? 0
            : MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1, 1)) * 180 / MathF.PI;

    /// <summary>
    /// The vector's horizontal part at unit length; zero for a vertical or near-zero vector.
    /// </summary>
    static Vector3 Horizontal(Vector3 v)
    {
        var h = new Vector3(v.X, 0, v.Z);
        return h.LengthSquared() < 1e-9f ? Vector3.Zero : Vector3.Normalize(h);
    }

    /// <summary>
    /// The quaternion at unit length; the identity for a near-zero one.
    /// </summary>
    static Quaternion Normalised(Quaternion q) => q.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(q);

    #endregion
}