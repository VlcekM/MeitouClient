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
    public enum BirdState { Gone = 0, Return = 1, Wander = 2, Follow = 3, Circle = 4, Attracted = 5, Rest = 6 }

    sealed class Bird
    {
        public readonly long Id = DebrisPiece.NewId();
        public Vector3 Position, Forward, Target;
        public float Timer, Radius, Altitude, Scale;
        public BirdState State;
        public Bird? Mate;
        public Attractor? Attractor;
    }

    sealed class Attractor(object key)
    {
        public readonly object Key = key;
        public Vector3 Position;
        public float Strength;
        public int Max, Count;
    }

    sealed class Group(BirdFlockData kind, Func<int, int, string?> groupOfCell)
    {
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

        static int[] BuildAmounts(BirdFlockData kind, Func<int, int, string?> groupOfCell)
        {
            var amounts = new int[WeatherAreas.Cells * WeatherAreas.Cells];
            for (int z = 0; z < WeatherAreas.Cells; z++)
                for (int x = 0; x < WeatherAreas.Cells; x++)
                    amounts[z * WeatherAreas.Cells + x] = groupOfCell(x, z) is { } id && kind.Lists(id) ? Math.Max(kind.AmountIn(id), 0) : -1;
            return amounts;
        }

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

        public static int Sum(int[] sums, int minX, int minZ, int maxX, int maxZ)
        {
            const int w = WeatherAreas.Cells + 1;
            return sums[(maxZ + 1) * w + maxX + 1] - sums[minZ * w + maxX + 1] - sums[(maxZ + 1) * w + minX] + sums[minZ * w + minX];
        }

        public readonly List<Bird> Birds = [];
        public readonly List<Attractor> Attractors = [];
        /// <summary>
        /// The mesh with its texture, as the debris renderer takes it.
        /// </summary>
        public readonly string Mesh = kind.Texture.Length == 0 ? kind.Mesh : kind.Mesh + DebrisPiece.TextureMark + kind.Texture;
    }

    const float FarSpawn = 1000, NeighbourRadius = 100, WideNeighbourRadius = 1000, FrustumMargin = -10;
    const float TooLow = 30, ClimbRate = 5, HeightScale = 2000, TurnPi = 3.14159f, FollowOffset = 100;

    readonly Group[] _groups;
    readonly Func<float, float, float> _ground;
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

    static int? AmountOf(Group g, int x, int z) => g.Amounts[z * WeatherAreas.Cells + x] is >= 0 and var amount ? amount : null;

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

    static bool InsideArea(Group g, Vector3 p)
    {
        var (cx, cz) = WeatherAreas.CameraCellOf(p.X, p.Z);
        return g.Amounts[cz * WeatherAreas.Cells + cx] >= 0;
    }

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

    static void ReleaseAttractor(Bird b)
    {
        if (b.Attractor is null) return;
        b.Attractor.Count--;
        b.Attractor = null;
        if (b.State == BirdState.Attracted) b.State = BirdState.Wander;
    }

    // ---- State timers

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

    static void Wander(Group g, Bird b)
    {
        b.Mate = FindMate(g, b, WideNeighbourRadius);
        b.State = b.Mate is null ? BirdState.Wander : BirdState.Follow;
    }

    float Next() => (float)_random.NextDouble();
    float Range(float min, float max) => min + Next() * (max - min);

    static Vector3 Normalise(Vector3 v) => v.LengthSquared() < 1e-12f ? v : Vector3.Normalize(v);

    /// <summary>
    /// This frame's birds as loose meshes, appended to <paramref name="pieces"/> (the mesh faces along its own +z); <paramref name="ready"/> says per
    /// mesh whether it is loaded (null: all are).
    /// </summary>
    public void AddPieces(List<DebrisPiece> pieces, Func<string, bool>? ready = null)
    {
        foreach (var g in _groups)
        {
            if (g.Kind.Mesh.Length == 0 || g.Birds.Count == 0 || ready is not null && !ready(g.Mesh)) continue;
            foreach (var b in g.Birds) pieces.Add(new DebrisPiece(g.Mesh, b.Scale, Orientation(b.Forward), b.Position, Id: b.Id));
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
    readonly Action _work;
    readonly SemaphoreSlim _go = new(0);
    volatile bool _busy;
    Exception? _failure;

    public BirdThread(Action work)
    {
        this._work = work;
        new Thread(Run) { IsBackground = true, Name = "Birds" }.Start();
    }

    public bool Busy => _busy;

    /// <summary>
    /// Runs the work once on the thread; the caller waits for <see cref="Busy"/> to clear first. A step that failed throws here, on the host's
    /// thread.
    /// </summary>
    public void Start()
    {
        if (_failure is { } e) { _failure = null; throw new InvalidOperationException("The bird thread failed.", e); }
        _busy = true;
        _go.Release();
    }

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