using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Particles;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Post;
using Meitou.Simulation;

namespace Meitou.Game.Effects;

/// <summary>
/// Meitou decapitation's drawing (docs/game/character-stats.md "Decapitation (Meitou)"): the falling head and the neck's spurts of blood.
/// </summary>
sealed class Decapitation(GameHost host)
{
    #region Falling heads

    // Cut-off heads (Meitou decapitation, docs/game/character-stats.md "Decapitation (Meitou)"): drawn by the character renderer, not the
    // simulation's. When a character the renderer drew with its head last frame is listed headless, its head falls from where it was: the look's
    // CharacterAppearance.SeveredHead, holding the bones of that last frame (taken at once, Rendering.Characters.CharacterRenderer.HoldPose), placed
    // by the body's placement then, turned about the head bone and dropped until it meets the ground or a body lying under it. The fall's clock
    // starts once the head's look is ready (so it is seen falling), timed by the shown tick as the severed limbs are (still while paused). Not saved:
    // a load or a new game clears them; past the dropped-items limit the oldest goes.
    /// <summary>
    /// One cut-off head: its draw key, look and pose, where and how it falls, and where it landed.
    /// </summary>
    sealed class FallingHead
    {
        /// <summary>
        /// The head's own draw key (the body's with bit 54 flipped) and the body's draw key, whose held pose it keeps.
        /// </summary>
        public required long Key, From;
        /// <summary>
        /// The severed head's look (<see cref="CharacterAppearance.SeveredHead"/>).
        /// </summary>
        public required CharacterAppearance Look;
        /// <summary>
        /// The body's placement and its head bone's place when it was cut.
        /// </summary>
        public required Matrix4x4 World;
        /// <summary>
        /// Where the head bone was when it was cut, and the horizontal axis it turns about while falling.
        /// </summary>
        public required Vector3 Start, Axis;
        /// <summary>
        /// Where it drifts to across the ground (x, z).
        /// </summary>
        public required Vector2 Aside;
        /// <summary>
        /// The speed it is thrown with (units/s): the blow that cut it off's, as the body's ragdoll (<see cref="CharacterSnapshot.Knockdown"/>).
        /// </summary>
        public required Vector3 Velocity;
        /// <summary>
        /// The seconds its drop takes to the ground under where it was cut.
        /// </summary>
        public required float Flight;
        /// <summary>
        /// The tumble's speed in the air (radians/s): less after each landing.
        /// </summary>
        public required float Spin;
        /// <summary>
        /// The body's blood amounts when it was cut, drawn on the head.
        /// </summary>
        public required ulong Blood;
        /// <summary>
        /// The shown tick its fall started (NaN until its look is drawn).
        /// </summary>
        public double Tick = double.NaN;
        /// <summary>
        /// Its motion (<see cref="Step"/>): the seconds of its fall stepped so far, where the head bone is, its speed, how it is turned about the head
        /// bone, its tumble's speed in the air, and whether it has come to rest.
        /// </summary>
        public float Time;
        public Vector3 At, Speed;
        public Quaternion Turn = Quaternion.Identity;
        public bool Started, Resting;
    }

    /// <summary>
    /// A head on the ground: what a landing keeps of its fall's speed (below <see cref="BounceLeast"/> units/s it stays down) and takes of its speed
    /// across the ground, how fast a rolling head slows (units/s2; a head is no ball), what a landing keeps of the tumble, and the speed it rests at.
    /// </summary>
    const float Bounce = 0.32f, BounceLeast = 6, LandingGrip = 0.3f, RollSlowing = 28, TumbleKept = 0.5f, RestSpeed = 0.4f;
    /// <summary>
    /// The longest step of a head's motion, and the most seconds caught up in one frame.
    /// </summary>
    const float StepSeconds = 1f / 120, CatchUp = 0.25f;

    /// <summary>
    /// The heads falling or lying, the oldest first; past the dropped-items limit the oldest goes.
    /// </summary>
    readonly List<FallingHead> _heads = [];
    /// <summary>
    /// The characters whose cut was looked at (a head spawned, or none to spawn: drawn headless already, or not drawn), for <see
    /// cref="_headsEpoch"/>.
    /// </summary>
    readonly HashSet<CharacterId> _headsSeen = [];
    /// <summary>
    /// The <see cref="GameSaves.WorldEpoch"/> the heads and spurts belong to: a load or a new world clears them.
    /// </summary>
    long _headsEpoch = -1;
    /// <summary>
    /// How far above what it lies on the head bone rests (about a head's half height).
    /// </summary>
    const float HeadRestHeight = 1.3f;
    /// <summary>
    /// A body's bones a falling head can land on (the torso thicker than the limbs), and how far from a bone it still lands on it.
    /// </summary>
    static readonly (string Bone, float Radius)[] BodyBones =
    [
        ("Bip01 Pelvis", 2.2f), ("Bip01 Spine1", 2.2f), ("Bip01 Spine2", 2.4f), ("Bip01 Neck", 1.2f), ("Bip01 Head", 1.4f),
        ("Bip01 L Thigh", 1.3f), ("Bip01 R Thigh", 1.3f), ("Bip01 L Calf", 1f), ("Bip01 R Calf", 1f),
        ("Bip01 L UpperArm", 1f), ("Bip01 R UpperArm", 1f), ("Bip01 L Forearm", 0.8f), ("Bip01 R Forearm", 0.8f),
    ];
    /// <summary>
    /// A bone this low over the ground is lying down (the head lands on it); a standing body's are higher and let it fall past.
    /// </summary>
    const float LyingBelow = 6;

    /// <summary>
    /// A headless character listed this frame: its head falls if the renderer drew it with one in its last update.
    /// </summary>
    internal void CutHead(CharacterSnapshot c, CharacterAppearance look)
    {
        if (!look.Headless || host.Gpu.Characters is not { } characters || !_headsSeen.Add(c.Id)) return;
        long from = host.DrawKey(c.Id), key = from ^ (1L << 54);
        if (characters.LastDrawn(from, "Bip01 Head") is not ({ } world, { Headless: false } before, var start) || !characters.HoldPose(key, from))
            return;
        // A look, not the simulation's: no world roll.
        ulong roll = unchecked((ulong)from * 0x9E3779B97F4A7C15UL ^ (ulong)host.Session.ShownTick * 0xBF58476D1CE4E5B9UL);
        roll ^= roll >> 31;
        float angle = (roll & 0xFFFF) / 65536f * MathF.Tau, reach = 3 + ((roll >> 16) & 0xFFFF) / 65536f * 4;
        var thrown = c.Knockdown;
        // Thrown: it tumbles over the way it flies, faster the faster it goes.
        var flat = new Vector2(thrown.X, thrown.Z);
        if (flat.LengthSquared() > 1) angle = MathF.Atan2(flat.Y, flat.X);
        var head = new FallingHead
        {
            Key = key,
            From = from,
            Look = before.SeveredHead(),
            World = world,
            Start = start,
            Aside = new Vector2(start.X + MathF.Cos(angle) * reach, start.Z + MathF.Sin(angle) * reach),
            Axis = new Vector3(-MathF.Sin(angle), 0, MathF.Cos(angle)),
            Spin = 2.5f + ((roll >> 32) & 0xFFFF) / 65536f * 3 + flat.Length() * 0.1f,
            Velocity = thrown,
            Flight = MathF.Sqrt(2 * MathF.Max(start.Y - host.Session.World.Walkability.GroundHeight(start.X, start.Z, start.Y) - HeadRestHeight,
                0) / Debris.FallGravity),
            Blood = c.BloodAmounts,
        };
        _heads.Add(head);
        characters.Prefetch(head.Look);
        Spurt(from, look, start);
        while (_heads.Count > Math.Max(host.Config.GroundItemLimit, 1)) _heads.RemoveAt(0);
    }

    /// <summary>
    /// The heads, each placed where its fall has it now: the head bone along the drop, the whole head turned about it until it lands.
    /// </summary>
    internal void AddHeads(CharacterDrawList list)
    {
        if (_headsEpoch != host.Saves.WorldEpoch)
        {
            _heads.Clear();
            _headsSeen.Clear();
            _spurts.Clear();
            _headsEpoch = host.Saves.WorldEpoch;
        }
        UpdateSpurts();
        if (_heads.Count == 0 || host.Gpu.Characters is not { } characters) return;
        double clock = host.Session.ShownTick + host.Session.SimulationAlpha;
        float tick = host.Session.World.TickSeconds;
        foreach (var h in _heads)
        {
            // Its clock starts with the first frame its look is drawn; until then it is listed (so it gets ready) where it was cut.
            if (double.IsNaN(h.Tick) && characters.IsPrepared(h.Look)) h.Tick = clock;
            float t = double.IsNaN(h.Tick) ? 0 : MathF.Max(0, (float)((clock - h.Tick) * tick));
            if (!h.Started)
            {
                // Thrown with the blow's speed, and aside over the time of its drop.
                var aside = (h.Aside - new Vector2(h.Start.X, h.Start.Z)) / MathF.Max(h.Flight, 0.2f);
                (h.At, h.Speed, h.Started) = (h.Start, h.Velocity + new Vector3(aside.X, 0, aside.Y), true);
            }

            if (!h.Resting && t > h.Time)
            {
                h.Time = MathF.Max(h.Time, t - CatchUp);
                float floor = SurfaceUnder(characters, new Vector2(h.At.X, h.At.Z), h) + HeadRestHeight;
                while (h.Time < t && !h.Resting)
                {
                    float dt = MathF.Min(StepSeconds, t - h.Time);
                    Step(h, dt, floor);
                    h.Time += dt;
                }
            }

            var world = h.World * Matrix4x4.CreateTranslation(-h.Start) * Matrix4x4.CreateFromQuaternion(h.Turn) * Matrix4x4.CreateTranslation(h.At);
            list.Add(new CharacterInstance(h.Key, h.Look, h.At, 0, []) { World = world, FreezeFrom = h.From, Blood = h.Blood });
        }
    }

    /// <summary>
    /// One step of a head's motion over what lies <paramref name="floor"/> high under it: it falls and tumbles; a landing throws it back up with a
    /// share of its speed and takes some of its speed across the ground; on the ground it rolls the way it goes, turning as a ball of its size
    /// would, slowing until it rests. Engine choice (the decapitation is Meitou's): a ball over a height, not a body in the physics scene.
    /// </summary>
    static void Step(FallingHead h, float dt, float floor)
    {
        h.Speed.Y -= Debris.FallGravity * dt;
        h.At += h.Speed * dt;
        var across = new Vector2(h.Speed.X, h.Speed.Z);
        bool down = h.At.Y <= floor;
        if (down)
        {
            h.At.Y = floor;
            if (h.Speed.Y < -BounceLeast)
            {
                h.Speed.Y *= -Bounce;
                across *= 1 - LandingGrip;
                h.Spin *= TumbleKept;
            }
            else
            {
                h.Speed.Y = 0;
                float speed = across.Length(), slowed = MathF.Max(speed - RollSlowing * dt, 0);
                across = speed > 1e-4f ? across * (slowed / speed) : Vector2.Zero;
                if (slowed < RestSpeed) (across, h.Resting) = (Vector2.Zero, true);
            }

            (h.Speed.X, h.Speed.Z) = (across.X, across.Y);
        }

        // In the air it tumbles as it was cut; on the ground it turns about the axis across its way, by the ground it covers.
        float rolling = across.Length();
        var (axis, turn) = down && h.Speed.Y == 0
            ? (rolling > 1e-4f ? new Vector3(across.Y, 0, -across.X) / rolling : h.Axis, rolling / HeadRestHeight * dt)
            : (h.Axis, h.Spin * dt);
        if (turn != 0) h.Turn = Quaternion.Normalize(Quaternion.Concatenate(h.Turn, Quaternion.CreateFromAxisAngle(axis, turn)));
    }

    /// <summary>
    /// The top of what lies at (x, z) under the falling head <paramref name="head"/>: the ground (the walkable surface below where it was cut:
    /// terrain, floor), or the highest bone of a body lying there (one within <see cref="LyingBelow"/> of the ground, as the renderer drew it last
    /// frame) plus that bone's thickness. Only what is below where the fall began counts (a head cut from a body already down starts among its
    /// bones), and never its own corpse's neck and head, where it was cut. Engine choice: the bodies are spheres round their bones, not their meshes
    /// or ragdoll shapes.
    /// </summary>
    float SurfaceUnder(Rendering.Characters.CharacterRenderer characters, Vector2 at, FallingHead head)
    {
        float ground = host.Session.World.Walkability.GroundHeight(at.X, at.Y, head.Start.Y), top = ground;
        foreach (var c in host.Session.CurrentSnapshot.Characters)
        {
            if (Vector2.DistanceSquared(new Vector2(c.Position.X, c.Position.Z), at) > 30 * 30) continue;
            long key = host.DrawKey(c.Id);
            foreach (var (bone, radius) in BodyBones)
            {
                if (key == head.From && bone is "Bip01 Neck" or "Bip01 Head") continue;
                if (characters.BonePosition(key, bone) is not { } p || p.Y >= ground + LyingBelow
                        || Vector2.DistanceSquared(new Vector2(p.X, p.Z), at) >= radius * radius)
                    continue;
                float surface = p.Y + radius * 0.6f;
                if (surface + HeadRestHeight < head.Start.Y) top = MathF.Max(top, surface);
            }
        }
        return top;
    }

    #endregion

    #region Neck spurts

    // The blood a cut-off head leaves (Meitou decapitation, docs/game/character-stats.md "Decapitation (Meitou)"): with the <c>Blood</c> setting on
    // and a race that bleeds, the neck spurts SpurtPulses times, weaker each time, the luckyren burst's spray (Debris.BurstSpray) thrown out of the
    // neck along the spine (from <c>Bip01 Spine2</c> to <c>Bip01 Neck</c>, as the body is drawn then), each pulse leaving a splash on the ground
    // where it lands. While a pulse emits, its emitter is moved and turned with the neck every frame, so the spray follows the falling body; what it
    // threw stays in the air. Timed by the shown tick (still while paused). Display only, not saved.
    /// <summary>
    /// One neck's spurts of blood: the body it follows, the blood's colour, the neck's place and the pulses left.
    /// </summary>
    sealed class NeckSpurt
    {
        /// <summary>
        /// The draw key of the body whose neck spurts.
        /// </summary>
        public required long From;
        /// <summary>
        /// The blood decal colour (opaque ABGR).
        /// </summary>
        public required uint Colour;
        /// <summary>
        /// The spray's colour for a race whose blood is not red (<see cref="BloodDecals.BloodRecolour"/>).
        /// </summary>
        public Vector3? Recolour;
        /// <summary>
        /// Where the neck was and which way the stump pointed (spine to neck) when the body was last drawn.
        /// </summary>
        public required Vector3 Neck;
        /// <summary>
        /// The stump's unit direction, spine to neck (up until the body is drawn).
        /// </summary>
        public Vector3 Along = Vector3.UnitY;
        /// <summary>
        /// The shown tick of the next pulse, and how many are left.
        /// </summary>
        public double Next = double.NaN;
        /// <summary>
        /// The pulses left.
        /// </summary>
        public int Left = SpurtPulses;
        /// <summary>
        /// The pulse emitting now (null: none) and its spray's fixed offset from the stump's line.
        /// </summary>
        public EffectUnit? Emitting;
        /// <summary>
        /// The random offset of the emitting pulse's spray from the stump's line.
        /// </summary>
        public Vector3 Jitter;
    }

    /// <summary>
    /// The necks spurting.
    /// </summary>
    readonly List<NeckSpurt> _spurts = [];
    /// <summary>
    /// Each race's blood data by race id, read once (the bleed rate and the blood colour).
    /// </summary>
    readonly Dictionary<string, RaceData> _bloodRaces = new(StringComparer.Ordinal);
    /// <summary>
    /// The pulses a neck spurts, each weaker than the one before.
    /// </summary>
    const int SpurtPulses = 6;
    /// <summary>
    /// Seconds between pulses, and how far (units) the first pulse's blood lands from the neck.
    /// </summary>
    const float SpurtGap = 0.35f, SpurtReach = 18;

    /// <summary>
    /// Starts the neck's spurts for the character drawn as <paramref name="from"/>, whose head was at <paramref name="head"/>.
    /// </summary>
    void Spurt(long from, CharacterAppearance look, Vector3 head)
    {
        if (host.BloodDecals.DecalStore is null || host.Scene.Database is not { } db) return;
        if (!_bloodRaces.TryGetValue(look.Race.StringId, out var race)) _bloodRaces[look.Race.StringId] = race = RaceData.From(look.Race, db);
        if (race.BleedRate <= 0) return;
        var recolour = BloodDecals.BloodRecolour(race.BloodColour);
        _spurts.Add(new NeckSpurt { From = from, Colour = 0xFF000000 | BloodDecals.Abgr(race.BloodColour), Recolour = recolour,
            Neck = host.Gpu.Characters?.BonePosition(from, "Bip01 Neck") ?? head });
        host.Gpu.Particles?.Burst(Debris.BurstSplash, head, Quaternion.Identity, 0.2f, 1.2f, 1.5f, recolour);
    }

    /// <summary>
    /// Follows the neck with the pulse emitting now and fires the pulses that are due.
    /// </summary>
    void UpdateSpurts()
    {
        if (_spurts.Count == 0) return;
        double clock = host.Session.ShownTick + host.Session.SimulationAlpha;
        float tick = host.Session.World.TickSeconds;
        for (int i = _spurts.Count - 1; i >= 0; i--)
        {
            var s = _spurts[i];
            if (double.IsNaN(s.Next)) s.Next = clock;
            TrackNeck(s);
            if (s.Left > 0 && clock >= s.Next)
            {
                Pulse(s);
                s.Next += SpurtGap / MathF.Max(tick, 1e-4f);
                s.Left--;
            }
            else if (s.Emitting is { Stopped: false } unit)
                host.Gpu.Particles?.MoveBurst(unit, s.Neck, Debris.Turn(Vector3.UnitZ, SprayDirection(s)));
            // Done once the last pulse has emitted (or a gap after it: particles turned off stop the units' clock).
            if (s.Left <= 0 && (s.Emitting is null or { Stopped: true } || clock >= s.Next)) _spurts.RemoveAt(i);
        }
    }

    /// <summary>
    /// The neck and the stump's direction as the body was drawn last; kept from the last time while it is not drawn.
    /// </summary>
    void TrackNeck(NeckSpurt s)
    {
        if (host.Gpu.Characters is not { } characters || characters.BonePosition(s.From, "Bip01 Neck") is not { } neck) return;
        s.Neck = neck;
        if (characters.BonePosition(s.From, "Bip01 Spine2") is { } spine && Vector3.DistanceSquared(neck, spine) > 1e-4f)
            s.Along = Vector3.Normalize(neck - spine);
    }

    /// <summary>
    /// Thrown out of the neck, a little off the spine's line, and upward enough to arc.
    /// </summary>
    static Vector3 SprayDirection(NeckSpurt s) => Vector3.Normalize(s.Along + s.Jitter + new Vector3(0, 0.3f, 0));

    /// <summary>
    /// Fires one pulse: a spray out of the neck at its strength (the pulses left out of all) and its splash on the ground.
    /// </summary>
    void Pulse(NeckSpurt s)
    {
        float strength = s.Left / (float)SpurtPulses;
        s.Jitter = new Vector3(host.BloodDecals.BloodRandom.NextSingle() - 0.5f, host.BloodDecals.BloodRandom.NextSingle() * 0.5f,
            host.BloodDecals.BloodRandom.NextSingle() - 0.5f) * 0.6f;
        var dir = SprayDirection(s);
        s.Emitting = host.Gpu.Particles?.Burst(Debris.BurstSpray, s.Neck, Debris.Turn(Vector3.UnitZ, dir), 0.15f + 0.25f * strength,
            0.8f + 0.6f * strength, 1.5f, s.Recolour);
        SpurtSplash(s, dir, strength);
    }

    /// <summary>
    /// The pulse's splash on the ground, out along the spray, nearer for a weaker pulse.
    /// </summary>
    void SpurtSplash(NeckSpurt s, Vector3 dir, float strength)
    {
        if (host.BloodDecals.DecalStore is not { } store) return;
        var flat = new Vector2(dir.X, dir.Z);
        flat = flat.LengthSquared() > 1e-6f ? Vector2.Normalize(flat) : host.Debris.RandomDirection();
        float far = SpurtReach * strength * (0.5f + host.BloodDecals.BloodRandom.NextSingle() * 0.5f);
        var at = s.Neck + new Vector3(flat.X, 0, flat.Y) * far;
        at.Y = host.Session.World.Walkability.GroundHeight(at.X, at.Z, s.Neck.Y);
        float size = 0.15f + 0.25f * strength;
        if (host.Gpu.Decals is { Surfaces: true } surfaces)
            surfaces.AddSurface(TerrainDecalType.Splash, at, flat, size, s.Colour, host.BloodDecals.BloodRandom);
        if (at.Y <= host.Gpu.Terrain.HeightAt(at.X, at.Z) + BloodDecals.BloodAboveGround)
            store.Add(TerrainDecalType.Splash, at, flat, size, s.Colour, TerrainDecalStore.NewLife, host.BloodDecals.BloodRandom);
    }

    #endregion
}