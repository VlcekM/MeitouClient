using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Game.Rendering.Objects;

namespace Meitou.Game.Effects;

/// <summary>
/// A world object that draws birds of one WILDLIFE_BIRDS kind to it (a record's <c>bird attractor</c> reference: <paramref name="Range"/> its first
/// value, <paramref name="Max"/> its second).
/// </summary>
public readonly record struct BirdAttractor(object Key, string Kind, Vector3 Position, int Range, int Max);

/// <summary>
/// The birds (docs/game/birds.md): the original's <c>BirdManager</c> thread step <c>FUN_140262830</c>, run on the display's real time. Per
/// WILDLIFE_BIRDS kind: spawn a flock when the birds alive plus its <c>spawn count</c> fit the amounts of the BIOME_GROUP cells in range
/// (<c>FUN_140260620</c>, <c>FUN_140262500</c>), then move each bird by its state (<see cref="BirdState"/>) with a turn limited to about one radian
/// per second. Birds are drawn as loose meshes (<see cref="DebrisPiece"/>).
/// </summary>
public sealed class BirdFlocks
{
    /// <summary>
    /// A bird's state (<b>Verified</b>, the <c>+0x38</c> field of <c>FUN_140262830</c>; 8 is never set).
    /// </summary>
    public enum BirdState
    {
        /// <summary>
        /// 0: removed (beyond the kind's distance and outside the view); dropped at the end of the step.
        /// </summary>
        Gone = 0,
        /// <summary>
        /// 1: over a cell whose group does not list the kind; flies back toward where it left.
        /// </summary>
        Return = 1,
        /// <summary>
        /// 2: wanders, steering along the terrain's contour, and looks for a mate.
        /// </summary>
        Wander = 2,
        /// <summary>
        /// 3: follows a mate, 100 units beside it.
        /// </summary>
        Follow = 3,
        /// <summary>
        /// 4: circles a point that creeps along the terrain's slope (circling kinds).
        /// </summary>
        Circle = 4,
        /// <summary>
        /// 5: circles an attractor it took.
        /// </summary>
        Attracted = 5,
        /// <summary>
        /// 6: rests after an attractor (moves as <see cref="Wander"/>).
        /// </summary>
        Rest = 6
    }

    /// <summary>
    /// One bird: where it is and heads, its state and timer, its circle, its scale, the mate it follows and the attractor it holds.
    /// </summary>
    sealed class Bird
    {
        /// <summary>
        /// The bird's identity for the frame's draw (a fixed handle across frames).
        /// </summary>
        public readonly long Id = DebrisPiece.NewId();
        /// <summary>
        /// Where the bird is, its unit heading, and the point it returns to or circles.
        /// </summary>
        public Vector3 Position, Forward, Target;
        /// <summary>
        /// Seconds left in the state; the circle's radius and altitude (the ground height where it began); the mesh scale.
        /// </summary>
        public float Timer, Radius, Altitude, Scale;
        /// <summary>
        /// What the bird is doing.
        /// </summary>
        public BirdState State;
        /// <summary>
        /// The bird it follows; null for none.
        /// </summary>
        public Bird? Mate;
        /// <summary>
        /// The attractor it circles or rests from; null for none.
        /// </summary>
        public Attractor? Attractor;
    }

    /// <summary>
    /// One object that draws birds (<see cref="BirdAttractor"/>): its place, its pull and how many birds it takes.
    /// </summary>
    sealed class Attractor(object key)
    {
        /// <summary>
        /// The world object's key, matched across frames.
        /// </summary>
        public readonly object Key = key;
        /// <summary>
        /// Where the object is.
        /// </summary>
        public Vector3 Position;
        /// <summary>
        /// The pull: 100 x the range squared; a bird takes it while strength / distance squared is above 1.
        /// </summary>
        public float Strength;
        /// <summary>
        /// The most birds it takes, and how many it holds.
        /// </summary>
        public int Max, Count;
    }

    /// <summary>
    /// One WILDLIFE_BIRDS kind: its data, its amount per weather cell, the sums over them, its birds and attractors.
    /// </summary>
    sealed class Group(BirdFlockData kind, Func<int, int, string?> groupOfCell)
    {
        /// <summary>
        /// The WILDLIFE_BIRDS record's data.
        /// </summary>
        public readonly BirdFlockData Kind = kind;
        /// <summary>
        /// The kind's amount in each weather cell (-1: the cell's group does not list it), row z * 64 + x; the map does not change while the birds
        /// fly.
        /// </summary>
        public readonly int[] Amounts = BuildAmounts(kind, groupOfCell);
        /// <summary>
        /// Summed-area tables of the listing cells and their amounts (65 x 65: rows and columns of the cells before), so a rectangle's sums take four
        /// reads.
        /// </summary>
        public readonly int[] MatchSums = new int[(WeatherAreas.Cells + 1) * (WeatherAreas.Cells + 1)],
            TotalSums = new int[(WeatherAreas.Cells + 1) * (WeatherAreas.Cells + 1)];

        /// <summary>
        /// The kind's amount per weather cell from the cell's BIOME_GROUP (-1 where the group does not list it).
        /// </summary>
        static int[] BuildAmounts(BirdFlockData kind, Func<int, int, string?> groupOfCell)
        {
            var amounts = new int[WeatherAreas.Cells * WeatherAreas.Cells];
            for (int z = 0; z < WeatherAreas.Cells; z++)
                for (int x = 0; x < WeatherAreas.Cells; x++)
                    amounts[z * WeatherAreas.Cells + x] = groupOfCell(x, z) is { } id && kind.Lists(id) ? Math.Max(kind.AmountIn(id), 0) : -1;
            return amounts;
        }

        /// <summary>
        /// Fills <see cref="MatchSums"/> and <see cref="TotalSums"/> from <see cref="Amounts"/>.
        /// </summary>
        public void BuildSums()
        {
            const int n = WeatherAreas.Cells, w = n + 1;
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    int a = Amounts[z * n + x];
                    MatchSums[(z + 1) * w + x + 1] = MatchSums[z * w + x + 1] + MatchSums[(z + 1) * w + x] - MatchSums[z * w + x] + (a >= 0 ? 1 : 0);
                    TotalSums[(z + 1) * w + x + 1] = TotalSums[z * w + x + 1] + TotalSums[(z + 1) * w + x] - TotalSums[z * w + x] + Math.Max(a, 0);
                }
        }

        /// <summary>
        /// The sum of a summed-area table over the cells <paramref name="minX"/>..<paramref name="maxX"/> by
        /// <paramref name="minZ"/>..<paramref name="maxZ"/>.
        /// </summary>
        public static int Sum(int[] sums, int minX, int minZ, int maxX, int maxZ)
        {
            const int w = WeatherAreas.Cells + 1;
            return sums[(maxZ + 1) * w + maxX + 1] - sums[minZ * w + maxX + 1] - sums[(maxZ + 1) * w + minX] + sums[minZ * w + minX];
        }

        /// <summary>
        /// The kind's birds alive.
        /// </summary>
        public readonly List<Bird> Birds = [];
        /// <summary>
        /// The objects that draw the kind's birds this frame.
        /// </summary>
        public readonly List<Attractor> Attractors = [];
        /// <summary>
        /// The mesh with its texture, as the debris renderer takes it.
        /// </summary>
        public readonly string Mesh = kind.Texture.Length == 0 ? kind.Mesh : kind.Mesh + DebrisPiece.TextureMark + kind.Texture;
    }

    /// <summary>
    /// Spawns stay this far from the camera focus; the mate search radius while wandering and when a timer runs out; how far outside a frustum plane
    /// a bird may be before it can be removed (units).
    /// </summary>
    const float FarSpawn = 1000, NeighbourRadius = 100, WideNeighbourRadius = 1000, FrustumMargin = -10;
    /// <summary>
    /// Below this height over the ground a bird climbs at <c>ClimbRate</c>; the height error is divided by <c>HeightScale</c>; π for a turn of a
    /// reversed heading; a follower aims this far beside its mate (units).
    /// </summary>
    const float TooLow = 30, ClimbRate = 5, HeightScale = 2000, TurnPi = 3.14159f, FollowOffset = 100;

    /// <summary>
    /// The kinds that fly (a speed and a spawn count above 0).
    /// </summary>
    readonly Group[] _groups;
    /// <summary>
    /// The terrain's height at a world x, z.
    /// </summary>
    readonly Func<float, float, float> _ground;
    /// <summary>
    /// Every random draw of the step, seeded so a run repeats.
    /// </summary>
    readonly Random _random;

    /// <param name="ground">The terrain's height at a world x, z.</param>
    /// <param name="groupOfCell">The BIOME_GROUP id of a weather cell (null: none).</param>
    public BirdFlocks(IReadOnlyList<BirdFlockData> kinds, Func<float, float, float> ground, Func<int, int, string?> groupOfCell, int seed = 1)
    {
        _groups = [.. kinds.Where(k => k.Speed > 0 && k.SpawnCount > 0).Select(k => new Group(k, groupOfCell))];
        foreach (var g in _groups) g.BuildSums();
        this._ground = ground;
        _random = new Random(seed);
    }

    /// <summary>
    /// The birds alive.
    /// </summary>
    public int BirdCount => _groups.Sum(g => g.Birds.Count);

    /// <summary>
    /// Per kind (debug aid, the control channel's <c>birds</c>): the birds alive, those within <paramref name="near"/> units of <paramref name="eye"/>,
    /// the spawn rule's cell sum and listing cells in range, and the kind's data.
    /// </summary>
    public IEnumerable<(string Kind, int Alive, int Near, int Total, int Cells, float Distance, int SpawnCount)> Stats(Vector3 eye, float near) =>
        _groups.Select(g =>
        {
            var range = CellsAround(g, eye);
            return (g.Kind.Id, g.Birds.Count, g.Birds.Count(b => Vector3.Distance(b.Position, eye) <= near), range.Total, range.Matches, g.Kind.Distance, g.Kind.SpawnCount);
        });

    /// <summary>
    /// The birds in state <paramref name="state"/>.
    /// </summary>
    public int CountIn(BirdState state) => _groups.Sum(g => g.Birds.Count(b => b.State == state));

    /// <summary>
    /// Moves the birds by <paramref name="dt"/> real seconds. <paramref name="eye"/> is the camera's position, <paramref name="focus"/> the camera
    /// controller's (where new flocks look), and <paramref name="frustum"/> the view's six planes, normals inwards and normalised (a bird beyond its
    /// kind's <c>distance</c> and outside them is removed; null: none is).
    /// </summary>
    public void Update(float dt, Vector3 eye, Vector3 focus, Vector4[]? frustum)
    {
        foreach (var g in _groups)
        {
            var range = CellsAround(g, eye);
            if (g.Birds.Count + g.Kind.SpawnCount <= range.Total) SpawnFlock(g, range, focus);
            for (int i = 0; i < g.Birds.Count; i++)
                if (g.Birds[i].State != BirdState.Gone) Step(g, g.Birds[i], i, dt, eye, frustum);
            g.Birds.RemoveAll(b => b.State == BirdState.Gone);
        }
    }

    /// <summary>
    /// Gives the attractors of this frame (<c>FUN_140260880</c>: one the world no longer has is dropped, one it has gains its place with no birds on
    /// it).
    /// </summary>
    public void SetAttractors(IReadOnlyList<BirdAttractor> attractors)
    {
        foreach (var g in _groups)
        {
            var mine = new Dictionary<object, BirdAttractor>();
            foreach (var a in attractors)
                if (a.Kind == g.Kind.Id && a.Max > 0) mine[a.Key] = a;
            foreach (var gone in g.Attractors.Where(o => !mine.ContainsKey(o.Key)).ToList())
            {
                foreach (var b in g.Birds.Where(b => b.Attractor == gone)) ReleaseAttractor(b);
                g.Attractors.Remove(gone);
            }
            var held = g.Attractors.ToDictionary(o => o.Key);
            foreach (var a in mine.Values)
            {
                if (!held.TryGetValue(a.Key, out var current)) g.Attractors.Add(current = new Attractor(a.Key));
                (current.Position, current.Strength, current.Max) = (a.Position, a.Range * (float)a.Range * 100, a.Max);
            }
        }
    }

    // ---- Spawning (FUN_140260620, FUN_140262500, FUN_1402622d0)

    /// <summary>
    /// The weather cells within the kind's distance (clamped to the map): their bounds, how many list the kind, and the sum of their amounts.
    /// </summary>
    readonly record struct CellRange(int MinX, int MinZ, int MaxX, int MaxZ, int Matches, int Total);

    /// <summary>
    /// The cells within the kind's <c>distance</c> of the camera (clamped to the map) that list the kind, and the sum of their amounts.
    /// </summary>
    CellRange CellsAround(Group g, Vector3 eye)
    {
        var kind = g.Kind;
        var (minX, minZ) = WeatherAreas.CameraCellOf(eye.X - kind.Distance, eye.Z - kind.Distance);
        var (maxX, maxZ) = WeatherAreas.CameraCellOf(eye.X + kind.Distance, eye.Z + kind.Distance);
        return new CellRange(minX, minZ, maxX, maxZ, Group.Sum(g.MatchSums, minX, minZ, maxX, maxZ), Group.Sum(g.TotalSums, minX, minZ, maxX, maxZ));
    }

    /// <summary>
    /// The kind's amount in a cell; null when the cell's group does not list it.
    /// </summary>
    static int? AmountOf(Group g, int x, int z) => g.Amounts[z * WeatherAreas.Cells + x] is >= 0 and var amount ? amount : null;

    /// <summary>
    /// Spawns a flock of the kind's <c>spawn count</c> at a random listing cell in range, unless the point is within <see cref="FarSpawn"/> of the
    /// focus or outside the kind's spawn heights.
    /// </summary>
    void SpawnFlock(Group g, CellRange range, Vector3 focus)
    {
        var kind = g.Kind;
        int pick = _random.Next(0, range.Matches + 1);
        (int X, int Z)? cell = null;
        for (int x = range.MinX; x <= range.MaxX && pick >= 1; x++)
            for (int z = range.MinZ; z <= range.MaxZ && pick >= 1; z++)
                if (AmountOf(g, x, z) is not null)
                {
                    cell = (x, z);
                    pick--;
                }
        if (cell is not { } c) return;
        var (x0, z0, x1, z1) = WeatherAreas.CellRect(c.X, c.Z);
        float px = x0 + (x1 - x0) * Next(), pz = z0 + (z1 - z0) * Next(), py = _ground(px, pz);
        if (new Vector2(px - focus.X, pz - focus.Z).LengthSquared() < FarSpawn * FarSpawn || py <= kind.SpawnHeightMin || py >= kind.SpawnHeightMax)
            return;
        var at = new Vector3(px, py, pz);
        for (int i = 0; i < kind.SpawnCount; i++)
        {
            g.Birds.Add(new Bird
            {
                Position = at,
                Forward = Normalise(focus - at),
                State = BirdState.Wander,
                Timer = Range(30, 180),
                Scale = Range(kind.ScaleMin, kind.ScaleMax),
            });
            at += Normalise(new Vector3(Range(-1, 1), 0, Range(-1, 1))) * 5;
        }
    }

    // ---- The step (FUN_140262830)

    /// <summary>
    /// One bird's step: moves it, sends it back when it leaves the kind's cells, steers it by its state, takes an attractor, runs out its timer and
    /// removes it beyond the kind's distance outside the view.
    /// </summary>
    void Step(Group g, Bird b, int index, float dt, Vector3 eye, Vector4[]? frustum)
    {
        var kind = g.Kind;
        var stride = b.Forward * (dt * kind.Speed);
        b.Position += stride;
        bool inside = InsideArea(g, b.Position);
        if (!inside)
        {
            b.State = BirdState.Return;
            b.Timer = 1e6f;
            b.Target = b.Position - stride;
            ReleaseAttractor(b);
        }
        float groundY = _ground(b.Position.X, b.Position.Z);
        var desired = Desire(g, b, inside, dt, ref groundY);
        Turn(b, desired, index, groundY, dt, kind);
        if (b.State is BirdState.Wander or BirdState.Circle) TakeAttractor(g, b);
        b.Timer -= dt;
        if (b.Timer < 0) Expire(g, b, groundY);
        if (frustum is not null && Vector3.Distance(eye, b.Position) > kind.Distance && Outside(frustum, b.Position))
        {
            b.State = BirdState.Gone;
            ReleaseAttractor(b);
        }
    }

    /// <summary>
    /// Whether the point's weather cell lists the kind.
    /// </summary>
    static bool InsideArea(Group g, Vector3 p)
    {
        var (cx, cz) = WeatherAreas.CameraCellOf(p.X, p.Z);
        return g.Amounts[cz * WeatherAreas.Cells + cx] >= 0;
    }

    /// <summary>
    /// Whether the point is more than 10 units outside any of the frustum's planes.
    /// </summary>
    static bool Outside(Vector4[] frustum, Vector3 p)
    {
        foreach (var plane in frustum)
            if (plane.X * p.X + plane.Y * p.Y + plane.Z * p.Z + plane.W < FrustumMargin) return true;
        return false;
    }

    /// <summary>
    /// The terrain's unit normal at a position (finite differences of the height).
    /// </summary>
    Vector3 Normal(Vector3 p)
    {
        const float e = 16;
        float dx = _ground(p.X + e, p.Z) - _ground(p.X - e, p.Z), dz = _ground(p.X, p.Z + e) - _ground(p.X, p.Z - e);
        return Vector3.Normalize(new Vector3(-dx, 2 * e, -dz));
    }

    /// <summary>
    /// The direction the bird wants to fly in, by its state; a circling bird lifts <paramref name="groundY"/> to its altitude minus 500.
    /// </summary>
    Vector3 Desire(Group g, Bird b, bool inside, float dt, ref float groundY)
    {
        var kind = g.Kind;
        switch (b.State)
        {
            case BirdState.Return:
                {
                    float dz = b.Target.Z - b.Position.Z;
                    var aim = new Vector3(b.Target.X, b.Position.Y, b.Target.Z) + Normalise(new Vector3(dz, 0, b.Position.X - b.Target.X)) * dz;
                    if (inside) b.Timer = 0;
                    return aim - b.Position;
                }
            case BirdState.Wander or BirdState.Rest:
                {
                    var desired = b.Forward;
                    var normal = Normal(b.Position);
                    if (normal.Y < 0.999f)
                    {
                        var along = new Vector3(normal.Z, 0, -normal.X);
                        if (Vector3.Dot(along, b.Forward) < 0) along = -along;
                        float sign = kind.GroundHeight > groundY ? 1 : -1;
                        desired = along * kind.ContourStrength + Normalise(new Vector3(normal.X, 0, normal.Z)) * sign;
                    }
                    if ((b.Mate = FindMate(g, b, NeighbourRadius)) is not null) b.State = BirdState.Follow;
                    return desired;
                }
            case BirdState.Follow:
                {
                    if (FindMate(g, b, NeighbourRadius) is { } fresh) b.Mate = fresh;
                    return b.Mate is { } mate ? Follow(b, mate, dt) : b.Forward;
                }
            case BirdState.Circle or BirdState.Attracted:
                return Circle(b, dt, ref groundY, kind);
            default:
                return b.Forward;
        }
    }

    /// <summary>
    /// Head for the point 100 units beside the mate (on the bird's own side), and speed up or slow down by up to 30 percent of a unit per second to
    /// keep level with it.
    /// </summary>
    static Vector3 Follow(Bird b, Bird mate, float dt)
    {
        var side = new Vector3(mate.Forward.Z, 0, -mate.Forward.X);
        if (Vector3.Dot(side, b.Position - mate.Position) < 0) side = -side;
        var beside = mate.Position + side * FollowOffset;
        float ahead = Math.Clamp(Vector3.Dot(b.Forward, beside - mate.Forward * FollowOffset - b.Position), -100, 100);
        var desired = beside - b.Position;
        b.Position += b.Forward * ((ahead * 0.003f + 1) * dt);
        return desired;
    }

    /// <summary>
    /// The direction a circling bird wants: round its target (a point creeping along the slope, or the attractor's place) at its radius; lifts
    /// <paramref name="groundY"/> to the altitude minus 500.
    /// </summary>
    Vector3 Circle(Bird b, float dt, ref float groundY, BirdFlockData kind)
    {
        if (b.State == BirdState.Circle)
        {
            if (groundY > b.Altitude)
            {
                b.Target = b.Position;
                b.Altitude = groundY;
            }
            float sign = kind.Height > b.Altitude ? 1 : -1;
            var normal = Normal(b.Position);
            b.Target += Normalise(new Vector3(normal.X, 0, normal.Z)) * dt * sign;
        }
        else if (b.Attractor is { } held) b.Target = held.Position;
        var centre = new Vector3(b.Target.X, b.Position.Y, b.Target.Z);
        float dx = centre.X - b.Position.X, dz = centre.Z - b.Position.Z, reach = b.Radius * 1.4f;
        var push = dx * dx + dz * dz < reach * reach
            ? Normalise(b.Position + b.Forward * 100 - centre)
            : new Vector3(b.Forward.Z, 0, b.Forward.X);
        groundY = Math.Max(groundY, b.Altitude - 500);
        return centre + push * b.Radius - b.Position;
    }

    /// <summary>
    /// Settles the bird's height over the ground and turns its heading towards <paramref name="desired"/> by about a radian per second.
    /// </summary>
    static void Turn(Bird b, Vector3 desired, int index, float groundY, float dt, BirdFlockData kind)
    {
        desired = Normalise(desired);
        double scatter = index * 4.32167;
        float wobble = (float)(scatter - Math.Floor(scatter) - 0.5);
        float height = b.Position.Y - groundY;
        if (height < 0) b.Position.Y = groundY;
        if (TooLow > height) desired.Y = ClimbRate;
        else if (b.State != BirdState.Follow)
        {
            float want = (wobble * 0.3f + 1) * kind.Height;
            if (b.Mate is { } mate) want = mate.Position.Y - groundY + wobble * ClimbRate;
            desired.Y = (want - height) / HeightScale;
        }
        desired = Normalise(desired);
        float cos = Vector3.Dot(b.Forward, desired);
        if (cos > 0.99)
        {
            b.Forward = desired;
            return;
        }
        float angle = -1 < cos ? MathF.Acos(cos) : TurnPi;
        b.Forward = Normalise(b.Forward + (desired - b.Forward) * (dt / angle));
    }

    // ---- Neighbours and attractors (FUN_140260200, FUN_1402604f0)

    /// <summary>
    /// The nearest bird of a flocking kind that is ahead of this one's view of it (it moves away from this bird) within <paramref name="radius"/>,
    /// unless it already follows this bird.
    /// </summary>
    static Bird? FindMate(Group g, Bird self, float radius)
    {
        if (!g.Kind.Flocking) return null;
        float best = radius * radius;
        Bird? mate = null;
        foreach (var o in g.Birds)
        {
            if (o == self || o.State is BirdState.Circle or BirdState.Attracted or BirdState.Gone) continue;
            float d = Vector3.DistanceSquared(self.Position, o.Position);
            if (Vector3.Dot(o.Forward, o.Position - self.Position) > 0 && d < best) (mate, best) = (o, d);
        }
        for (var chain = mate; chain is not null; chain = chain.Mate)
            if (chain == self) return null;
        return mate;
    }

    /// <summary>
    /// Takes the attractor with the highest pull above 1 that is not full: state 5, timer 30 to 90, radius 400 to 600.
    /// </summary>
    void TakeAttractor(Group g, Bird b)
    {
        Attractor? best = null;
        float score = 1;
        foreach (var a in g.Attractors)
        {
            if (a.Count >= a.Max) continue;
            float s = a.Strength / Vector3.DistanceSquared(b.Position, a.Position);
            if (s > score) (best, score) = (a, s);
        }
        if (best is null) return;
        if (b.Attractor != best)
        {
            ReleaseAttractor(b);
            b.Attractor = best;
            best.Count++;
        }
        b.Timer = Range(30, 90);
        b.State = BirdState.Attracted;
        b.Radius = Range(400, 600);
    }

    /// <summary>
    /// Lets go of the attractor (a bird circling it wanders again).
    /// </summary>
    static void ReleaseAttractor(Bird b)
    {
        if (b.Attractor is null) return;
        b.Attractor.Count--;
        b.Attractor = null;
        if (b.State == BirdState.Attracted) b.State = BirdState.Wander;
    }

    // ---- State timers

    /// <summary>
    /// A state's timer ran out: the next state, timer and mate (docs/game/birds.md timers).
    /// </summary>
    void Expire(Group g, Bird b, float groundY)
    {
        switch (b.State)
        {
            case BirdState.Return or BirdState.Circle or BirdState.Rest:
                b.Timer = Range(10, 40);
                Wander(g, b);
                break;
            case BirdState.Wander or BirdState.Follow when !g.Kind.Circling:
                b.Timer = Range(5, 30);
                Wander(g, b);
                break;
            case BirdState.Wander or BirdState.Follow:
                b.State = BirdState.Circle;
                b.Target = b.Position;
                b.Timer = Range(30, 180);
                b.Altitude = groundY;
                b.Mate = null;
                b.Radius = Range(400, 600);
                break;
            case BirdState.Attracted:
                b.State = BirdState.Rest;
                b.Timer = Range(10, 40);
                ReleaseAttractor(b);
                break;
        }
    }

    /// <summary>
    /// Looks for a mate within <see cref="WideNeighbourRadius"/>: follows it, or wanders without one.
    /// </summary>
    static void Wander(Group g, Bird b)
    {
        b.Mate = FindMate(g, b, WideNeighbourRadius);
        b.State = b.Mate is null ? BirdState.Wander : BirdState.Follow;
    }

    /// <summary>
    /// A random value 0..1.
    /// </summary>
    float Next() => (float)_random.NextDouble();
    /// <summary>
    /// A random value <paramref name="min"/>..<paramref name="max"/>.
    /// </summary>
    float Range(float min, float max) => min + Next() * (max - min);

    /// <summary>
    /// The vector at unit length; a near-zero one as it is.
    /// </summary>
    static Vector3 Normalise(Vector3 v) => v.LengthSquared() < 1e-12f ? v : Vector3.Normalize(v);

    /// <summary>
    /// This frame's birds as loose meshes, appended to <paramref name="pieces"/> (the mesh faces along its own +z). <paramref name="centreOf"/> gives
    /// a mesh's bounds middle in its own space (null: not loaded, its birds are skipped): a piece is placed by its middle, a bird by its mesh's
    /// origin (the original's instanced entity on a scene node at the bird's position).
    /// </summary>
    public void AddPieces(List<DebrisPiece> pieces, Func<string, Vector3?> centreOf)
    {
        foreach (var g in _groups)
        {
            if (g.Kind.Mesh.Length == 0 || g.Birds.Count == 0 || centreOf(g.Mesh) is not { } centre) continue;
            foreach (var b in g.Birds)
            {
                var rotation = Orientation(b.Forward);
                pieces.Add(new DebrisPiece(g.Mesh, b.Scale, rotation, b.Position + Vector3.Transform(centre * b.Scale, rotation), Id: b.Id));
            }
        }
    }

    /// <summary>
    /// Ogre's <c>Quaternion::FromAxes</c> of x = up x forward, y = forward x x, z = forward.
    /// </summary>
    static Quaternion Orientation(Vector3 forward)
    {
        var x = Normalise(Vector3.Cross(Vector3.UnitY, forward));
        if (x == Vector3.Zero) x = Vector3.UnitX;
        var y = Vector3.Cross(forward, x);
        var m = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, forward.X, forward.Y, forward.Z, 0, 0, 0, 0, 1);
        return Quaternion.CreateFromRotationMatrix(m);
    }
}

/// <summary>
/// The thread the birds' step runs on (the original's <c>BirdManager</c> has its own, "Birds"): the host starts a step with <see cref="Start"/> once
/// the last one is done (<see cref="Busy"/>) and reads the flocks between steps only.
/// </summary>
public sealed class BirdThread
{
    /// <summary>
    /// The step the thread runs.
    /// </summary>
    readonly Action _work;
    /// <summary>
    /// Released by <see cref="Start"/> to run one step.
    /// </summary>
    readonly SemaphoreSlim _go = new(0);
    /// <summary>
    /// Set by <see cref="Start"/>, cleared when the step is done.
    /// </summary>
    volatile bool _busy;
    /// <summary>
    /// The exception of the last failed step, thrown by the next <see cref="Start"/>.
    /// </summary>
    Exception? _failure;

    /// <summary>
    /// Starts the background thread that runs <paramref name="work"/> on each <see cref="Start"/>.
    /// </summary>
    public BirdThread(Action work)
    {
        this._work = work;
        new Thread(Run) { IsBackground = true, Name = "Birds" }.Start();
    }

    /// <summary>
    /// Whether a step is running.
    /// </summary>
    public bool Busy => _busy;

    /// <summary>
    /// Runs the work once on the thread; the caller waits for <see cref="Busy"/> to clear first. A step that failed throws here, on the host's
    /// thread.
    /// </summary>
    public void Start()
    {
        if (_failure is { } e)
        {
            _failure = null;
            throw new InvalidOperationException("The bird thread failed.", e);
        }
        _busy = true;
        _go.Release();
    }

    /// <summary>
    /// The thread's loop: waits for a start, runs the work, keeps a failure for the host.
    /// </summary>
    void Run()
    {
        while (true)
        {
            _go.Wait();
            try
            {
                _work();
            }
            catch (Exception e)
            {
                _failure = e;
            }
            _busy = false;
        }
    }
}