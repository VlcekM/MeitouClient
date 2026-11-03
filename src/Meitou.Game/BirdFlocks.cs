using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Rendering;

namespace Meitou.Game;

/// <summary>
/// The birds (docs/game/birds.md): flocks of the WILDLIFE_BIRDS kinds the camera's BIOME_GROUP lists, flying round the camera on the display's real time, as the original's
/// <c>BirdManager</c> does on its own thread. The kinds, their numbers and the fields' meanings come from the original (<b>Verified</b> loader); the steering is Meitou's
/// own (the original's <c>FUN_140262830</c> states and constants are not reproduced). Birds are drawn as loose meshes (<see cref="DebrisPiece"/>).
/// </summary>
public sealed class BirdFlocks
{
    sealed class Bird { public Vector3 Offset, Position; public float Scale, Phase; }

    sealed class Flock
    {
        public BirdFlockData Kind = null!;
        public Vector3 Position;
        public float Heading, Turn;
        public readonly List<Bird> Birds = [];
    }

    readonly IReadOnlyList<BirdFlockData> kinds;
    readonly Func<float, float, float> ground;
    readonly List<Flock> flocks = [];
    readonly Random random;

    public BirdFlocks(IReadOnlyList<BirdFlockData> kinds, Func<float, float, float> ground, int seed = 1)
    {
        this.kinds = kinds;
        this.ground = ground;
        random = new Random(seed);
    }

    public int FlockCount => flocks.Count;

    /// <summary>The radius round the camera a kind keeps its flocks in.</summary>
    public static float RadiusOf(BirdFlockData kind) => MathF.Sqrt(Math.Max(kind.Distance, 0));

    /// <summary>Moves the flocks by <paramref name="dt"/> real seconds; <paramref name="group"/> is the BIOME_GROUP under the camera (null: none, the flocks fly off).</summary>
    public void Update(float dt, Vector3 camera, string? group)
    {
        dt = Math.Clamp(dt, 0, 0.1f);
        foreach (var kind in kinds)
        {
            int want = group is null || kind.SpawnCount <= 0 || kind.Speed <= 0 ? 0 : kind.AmountIn(group);
            int have = flocks.Count(f => f.Kind == kind);
            for (; have < want; have++) Spawn(kind, camera);
        }
        for (int i = flocks.Count - 1; i >= 0; i--)
        {
            var f = flocks[i];
            Step(f, dt, camera);
            bool far = Vector2.Distance(new(f.Position.X, f.Position.Z), new(camera.X, camera.Z)) > RadiusOf(f.Kind) * 1.2f;
            int wanted = group is null ? 0 : f.Kind.AmountIn(group);
            if (far || wanted == 0 && flocks.Count(o => o.Kind == f.Kind) > 0 && Vector2.Distance(new(f.Position.X, f.Position.Z), new(camera.X, camera.Z)) > RadiusOf(f.Kind) * 0.5f)
            {
                flocks.RemoveAt(i);
                if (wanted > 0) Spawn(f.Kind, camera);
            }
        }
    }

    void Spawn(BirdFlockData kind, Vector3 camera)
    {
        float radius = RadiusOf(kind), angle = Next() * MathF.Tau, away = radius * (0.4f + 0.5f * Next());
        float x = camera.X + MathF.Cos(angle) * away, z = camera.Z + MathF.Sin(angle) * away;
        float low = Math.Min(kind.SpawnHeightMin, kind.SpawnHeightMax), high = Math.Max(kind.SpawnHeightMin, kind.SpawnHeightMax);
        var f = new Flock
        {
            Kind = kind, Heading = Next() * MathF.Tau, Position = new Vector3(x, ground(x, z) + low + (high - low) * Next() * 0.25f, z),
            Turn = kind.Circling ? (Next() < 0.5f ? -1 : 1) * kind.Speed / Math.Max(radius * 0.3f, 1) : 0,
        };
        for (int b = 0; b < kind.SpawnCount; b++)
        {
            float spread = kind.Flocking ? 0.04f : 0.25f;
            var offset = new Vector3((Next() - 0.5f) * 2, (Next() - 0.5f) * 0.5f, (Next() - 0.5f) * 2) * (kind.Speed * spread * (1 + b * 0.15f));
            f.Birds.Add(new Bird { Offset = offset, Position = f.Position + offset, Scale = kind.ScaleMin + (kind.ScaleMax - kind.ScaleMin) * Next(), Phase = Next() * MathF.Tau });
        }
        flocks.Add(f);
    }

    void Step(Flock f, float dt, Vector3 camera)
    {
        var kind = f.Kind;
        if (!kind.Circling)
        {
            f.Turn += (Next() - 0.5f) * dt * 0.8f;
            f.Turn = Math.Clamp(f.Turn, -0.3f, 0.3f);
            // Out past two thirds of the radius the flock comes back towards the camera.
            var to = new Vector2(camera.X - f.Position.X, camera.Z - f.Position.Z);
            if (to.Length() > RadiusOf(kind) * 0.66f)
            {
                float want = MathF.Atan2(to.Y, to.X), delta = MathF.IEEERemainder(want - f.Heading, MathF.Tau);
                f.Turn = Math.Clamp(delta, -0.3f, 0.3f);
            }
        }
        f.Heading += f.Turn * dt;
        var forward = new Vector3(MathF.Cos(f.Heading), 0, MathF.Sin(f.Heading));
        f.Position.X += forward.X * kind.Speed * dt;
        f.Position.Z += forward.Z * kind.Speed * dt;
        // Up or down to the cruising height over the ground below, as fast as a quarter of the speed.
        float cruise = ground(f.Position.X, f.Position.Z) * kind.ContourStrength + ground(camera.X, camera.Z) * (1 - kind.ContourStrength) + kind.Height;
        f.Position.Y += Math.Clamp(cruise - f.Position.Y, -kind.Speed * 0.25f * dt, kind.Speed * 0.25f * dt);
        var side = new Vector3(-forward.Z, 0, forward.X);
        foreach (var b in f.Birds)
        {
            b.Phase += dt * 2.3f;
            var target = f.Position + side * b.Offset.X + forward * b.Offset.Z + Vector3.UnitY * (b.Offset.Y + MathF.Sin(b.Phase) * 10);
            b.Position = Vector3.Lerp(b.Position, target, Math.Min(1, dt * 3));
        }
    }

    float Next() => (float)random.NextDouble();

    /// <summary>This frame's birds as loose meshes, appended to <paramref name="pieces"/> (the mesh faces along its own +z: the bird flies towards where it is going).</summary>
    public void AddPieces(List<DebrisPiece> pieces)
    {
        foreach (var f in flocks)
        {
            if (f.Kind.Mesh.Length == 0) continue;
            var heading = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2 - f.Heading);
            var bank = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, f.Turn * 0.8f);
            foreach (var b in f.Birds) pieces.Add(new DebrisPiece(f.Kind.Mesh, b.Scale, bank * heading, b.Position));
        }
    }
}
