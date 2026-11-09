using System.Numerics;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Combat;

/// <summary>
/// A fighter's fight steering (docs/game/combat.md "Fight steering"): the band its fight state asks for (set by the <see cref="PursuitSystem"/> every
/// tick, as the original's states call <c>combatMovementOffensive</c>), the last check that it may steer at its target, and the velocity smoothing.
/// Derived state: not saved; hashed while in use.
/// </summary>
public sealed class SteerState
{
    /// <summary>
    /// Steering this tick (else the fighter walks paths or stands), at the target in <see cref="Target"/>.
    /// </summary>
    public bool Active;
    /// <summary>
    /// The slot it steers at (-1: none).
    /// </summary>
    public int Target = -1;

    /// <summary>
    /// Whom it faces (the original's look-at hand, <c>FUN_1402ae2e0</c>; -1: none, it faces <see cref="Target"/>): set by the attack start and the
    /// swing (its target), the defence update (the swing it follows, not during a rear block) and state 5 (its target); nothing clears it, so the
    /// waiting state 6 keeps the last one.
    /// </summary>
    public int Face = -1;

    /// <summary>
    /// The band of the centre distance beyond the two radii it keeps (min and max of <see cref="CombatSteering.Band"/>), the speed limit, and
    /// <c>powerThrough</c>.
    /// </summary>
    public float Min = 10, Max = 20, Limit = 200;
    /// <summary>
    /// The fight state's <c>powerThrough</c>: the crowd's push is turned to the side of its facing (<see cref="CombatSteering"/>).
    /// </summary>
    public bool PowerThrough;

    /// <summary>
    /// The tick of the next same-place and clear-line check, and its answer.
    /// </summary>
    public long LineTick;
    /// <summary>
    /// Whether the last check found the line to the target clear.
    /// </summary>
    public bool LineClear;

    /// <summary>
    /// The target of the path chase the fighter is on (-1: none) and the tick it may end at the earliest: a blocked line met at a new pick or in the
    /// footwork keeps it on its path for <see cref="PursuitSystem.PathChaseSeconds"/> even when the line clears (<c>FUN_14060c8b0</c>,
    /// <c>FUN_14060d6a0</c>). Hashed while chasing.
    /// </summary>
    public int ChaseTarget = -1;
    /// <summary>
    /// The tick the path chase may end at the earliest.
    /// </summary>
    public long PathUntil;

    /// <summary>
    /// The smoothing's last samples, newest first (<see cref="CombatSteering.FilterLength"/>).
    /// </summary>
    internal readonly float[] HistX = new float[CombatSteering.FilterLength], HistZ = new float[CombatSteering.FilterLength];

    /// <summary>
    /// Adds the steering state to <paramref name="h"/>: the band, the line check, the smoothing's samples and, while chasing, the chase.
    /// </summary>
    internal void Hash(ref StateHasher h)
    {
        h.Add(0x53746572);
        h.Add(Active);
        h.Add(Target);
        h.Add(Face);
        h.Add(Min);
        h.Add(Max);
        h.Add(Limit);
        h.Add(PowerThrough);
        h.Add(LineTick);
        h.Add(LineClear);
        for (int k = 0; k < CombatSteering.FilterLength; k++)
        {
            h.Add(HistX[k]);
            h.Add(HistZ[k]);
        }
    }

    /// <summary>
    /// Back to a standing start: the smoothing forgets (engine choice: the original's filters keep what they had from its last fight).
    /// </summary>
    internal void Reset()
    {
        Array.Clear(HistX);
        Array.Clear(HistZ);
    }
}

/// <summary>
/// The original's fight steering (<c>CombatMovementController::combatMovementUpdate</c>, <b>Observed</b>, docs/game/combat.md "Fight steering"): a
/// velocity built from forces each tick, a seek along the facing to keep the band, repulsion from the bodies round it (enemies and, twice as strong,
/// the rest), a dead zone, the side-step snap, the footwork speed caps and a smoothing filter. Running fighters (gap over 90) and those with no clear
/// line to the target walk paths instead (<see cref="PursuitSystem"/>).
/// </summary>
public static class CombatSteering
{
    /// <summary>
    /// The share of the repulsion kept (TOTALREPULSION_DAMPING) and of the off-axis part of the velocity (STRAFE_STATE_DAMPING).
    /// </summary>
    public const float RepulsionDamping = 0.105f, StrafeDamping = 0.105f;

    /// <summary>
    /// The weight of the non-enemies' repulsion against the enemies'.
    /// </summary>
    public const float AllyWeight = 2;

    /// <summary>
    /// The most bodies one repulsion sum takes, enemies and the rest together (engine choice: the nearest, docs/game/combat.md "Fight steering").
    /// </summary>
    public const int MaxRepulsions = 26;

    /// <summary>
    /// The nearest bodies the repulsion first gathers by distance alone, so a few out of the flocking list among them still leave it its 26 (engine
    /// choice).
    /// </summary>
    const int Gathered = 32;

    /// <summary>
    /// The radius the gathering looks in first (engine choice: in a crowd the nearest 32 stand within it; <see cref="Nearest"/>).
    /// </summary>
    const float NearFirst = 20;

    /// <summary>
    /// The flocking list's reach beyond the two radii and its height limit (<c>FUN_14085a070</c>).
    /// </summary>
    public const float FlockReach = 40, FlockHeight = 20;

    /// <summary>
    /// Speed caps in the footwork: sideways, backing off, and in all (not running).
    /// </summary>
    public const float SideSpeed = 18, BackSpeed = 16, FootworkCap = 18;

    /// <summary>
    /// The smoothing: samples and the weight ratio per 60 fps frame (<c>MotionFilter</c>, 10 and 0.8).
    /// </summary>
    public const int FilterLength = 10;
    /// <summary>
    /// The weight ratio between one smoothing sample and the next, per 60 fps frame (<see cref="FilterLength"/>).
    /// </summary>
    public const float FilterDecay = 0.8f;

    /// <summary>
    /// The band from a state's near and far (<c>combatMovementOffensive</c>): with m = 1.4 x the raw footprint, min = clamp(near - m, 1, far'), max =
    /// max(far', min + 5), far' = clamp(far - m, 0, 9999).
    /// </summary>
    public static (float Min, float Max) Band(float near, float far, float footprint)
    {
        float m = 1.5f * footprint;
        float f = Math.Clamp(far - m, 0, 9999);
        float min = MathF.Max(MathF.Min(near - m, f), 1);
        return (min, MathF.Max(f, min + 5));
    }

    /// <summary>
    /// Left out of the flocking list (<c>FUN_14085a070</c>, docs/game/combat.md "Attack slots and circling"): down, in a cage or a bed, or sitting on
    /// a chair or throne (<c>isSitting</c>; Meitou: resting on a seat). Engine reading: a carried body too.
    /// </summary>
    internal static bool OutOfFlock(in CharacterHot h, CharacterCold c) =>
        c.Medical is { Incapacitated: true } || c.IsImprisoned || c.InSomething == Locks.HeldState.Bed
        || !c.CarriedBy.IsNone || h.Task == (byte)CharacterTask.Rest && c.Ai?.SitClip is not null;

    /// <summary>
    /// A character's steering radius (<c>getRadius</c>): its footprint x its size.
    /// </summary>
    public static float Radius(CharacterCold? c) => CombatTuning.Footprint(c) * (c?.Medical?.ScaleK ?? 1);

    /// <summary>
    /// The fighter's own draws (<c>setupCombatMovementVariables</c>): ENEMY_DIST 15 + 30u, ALLY_DIST 1.4r + 30u, and the dead zone's STOP and START.
    /// </summary>
    public static (float Enemy, float Ally, float Stop, float Start) Draws(ulong seed, ulong key, CharacterCold cold)
    {
        float r = CombatTuning.Footprint(cold) * (cold.Medical?.ScaleK ?? 1);
        float U(ulong k) => CombatRolls.Float(seed, key, CombatRoll.Steering, k);
        float stop = 3 + 3 * U(2), start = 30 + 3 * stop + 12 * U(3);
        if (cold.Fighter is { IsAnimal: false } f)
        {
            float e = 1 - f.EncumbranceFactor;
            stop = e * 2 * stop + 1;
            start = e * 3 * start + 1;
        }
        stop = Math.Clamp(stop, 0, 20);
        start = MathF.Max(MathF.Min(start, 90), stop + 10);
        return (15 + 30 * U(0), 1.5f * r + 30 * U(1), stop, start);
    }

    /// <summary>
    /// The slots of the nearest bodies (<see cref="Gathered"/> at most), kept on the stack.
    /// </summary>
    [System.Runtime.CompilerServices.InlineArray(Gathered)]
    struct NearSlots
    {
        /// <summary>
        /// One slot.
        /// </summary>
        int _slot;
    }

    /// <summary>
    /// The squared distances of the nearest bodies, parallel to <see cref="NearSlots"/>.
    /// </summary>
    [System.Runtime.CompilerServices.InlineArray(Gathered)]
    struct NearDistances
    {
        /// <summary>
        /// One squared distance.
        /// </summary>
        float _distance;
    }

    /// <summary>
    /// The state of one fighter's crowd push: who it is, its reaches and facing, and the nearest bodies the grid query gathered.
    /// </summary>
    struct Push
    {
        /// <summary>
        /// The world the bodies are read from.
        /// </summary>
        public World World;
        /// <summary>
        /// The combat system, for the enemy test.
        /// </summary>
        public CombatSystem Combat;
        /// <summary>
        /// The fighter's slot, its target's (never gathered) and its faction.
        /// </summary>
        public int Me, Target, Faction;
        /// <summary>
        /// The fighter's position.
        /// </summary>
        public Vector3 At;
        /// <summary>
        /// The fighter's steering radius, its own footprint radius, the push reach of an enemy and of an ally, and the gathering's reach now.
        /// </summary>
        public float Radius, Own, EnemyReach, AllyReach, Reach;
        /// <summary>
        /// The fighter's facing (X/Z), the side a <c>powerThrough</c> push is turned to.
        /// </summary>
        public Vector2 Face;
        /// <summary>
        /// Whether the push is turned to the side of the facing (<see cref="SteerState.PowerThrough"/>).
        /// </summary>
        public bool Turn;

        /// <summary>
        /// Only bodies that push go in (the second, full pass), else every body near enough by distance alone.
        /// </summary>
        public bool Pushers;

        /// <summary>
        /// The nearest bodies so far, by squared distance, ascending (ties: the first seen first); <see cref="Overflow"/> once one was left out.
        /// </summary>
        public NearSlots Slots;
        /// <summary>
        /// The squared distances parallel to <see cref="Slots"/>.
        /// </summary>
        public NearDistances Distances;
        /// <summary>
        /// How many bodies are gathered, and how many may be.
        /// </summary>
        public int Count, Capacity;
        /// <summary>
        /// Whether a body was left out (the list was full and it was not nearer).
        /// </summary>
        public bool Overflow;

        /// <summary>
        /// Puts body <paramref name="c"/> at <paramref name="distance"/> (squared) into the sorted list, dropping the farthest when it is full.
        /// </summary>
        public void Add(int c, float distance)
        {
            if (Count == Capacity && distance >= Distances[Count - 1])
            {
                Overflow = true;
                return;
            }
            if (Count == Capacity)
            {
                Overflow = true;
                Count--;
            }
            int k = Count++;
            for (; k > 0 && Distances[k - 1] > distance; k--)
            {
                Slots[k] = Slots[k - 1];
                Distances[k] = Distances[k - 1];
            }
            Slots[k] = c;
            Distances[k] = distance;
        }
    }

    /// <summary>
    /// The push of body <paramref name="c"/> on the fighter (<c>getRepulsionVector</c>), false when it is out of the flocking list or too far to
    /// push.
    /// </summary>
    static bool Repulsion(ref Push p, int c, out Vector2 v, out bool enemy)
    {
        (v, enemy) = (Vector2.Zero, false);
        var table = p.World.Characters;
        ref readonly var o = ref table.Previous[c];
        if (table.Cold(c) is not { } oc || OutOfFlock(o, oc)) return false;
        float rc = Radius(oc);
        var away = new Vector2(p.At.X - o.Position.X, p.At.Z - o.Position.Z);
        float flatDistance = away.Length();
        if (flatDistance >= p.Own + rc + FlockReach) return false;   // not in the flocking list
        enemy = p.Combat.IsEnemyOf(p.World, c, oc, p.Me, p.Faction);
        float d = Vector3.Distance(o.Position, p.At);
        float magnitude = (1 - d / (rc + (enemy ? p.EnemyReach : p.AllyReach))) * 100;
        if (!(magnitude > 0) || flatDistance < 1e-4f) return false;
        v = away / flatDistance * magnitude;

        // powerThrough: the push is turned to the side of the facing it is on, its length kept (FUN_1402e9530).
        if (p.Turn)
        {
            var side = new Vector2(p.Face.Y, -p.Face.X);
            v = (Vector2.Dot(side, v) > 0 ? side : -side) * magnitude;
        }
        return true;
    }

    /// <summary>
    /// Adds body <paramref name="c"/>'s push to the enemies' or the allies' sum; 1 when it pushes, else 0.
    /// </summary>
    static int Add(ref Push p, int c, ref Vector2 enemies, ref Vector2 allies)
    {
        if (!Repulsion(ref p, c, out var v, out bool enemy)) return 0;
        if (enemy) enemies += v;
        else allies += v;
        return 1;
    }

    /// <summary>
    /// The grid query's visit: keeps body <paramref name="c"/> when it is alive, not the fighter or its target, within <see cref="FlockHeight"/> and
    /// the reach (and, in the pushers' pass, pushes).
    /// </summary>
    static void Gather(int c, ref Push p)
    {
        if (c == p.Me || c == p.Target) return;
        ref readonly var o = ref p.World.Characters.Previous[c];
        if (!o.Alive || MathF.Abs(o.Position.Y - p.At.Y) >= FlockHeight) return;
        float dx = o.Position.X - p.At.X, dz = o.Position.Z - p.At.Z;
        if (dx * dx + dz * dz >= p.Reach * p.Reach) return;
        if (p.Pushers && !Repulsion(ref p, c, out _, out _)) return;
        p.Add(c, Vector3.DistanceSquared(o.Position, p.At));
    }

    /// <summary>
    /// The nearest bodies <see cref="Gather"/> takes within <paramref name="reach"/> of <paramref name="me"/>: those of a circle of
    /// <see cref="NearFirst"/>, twice as wide while it does not hold <see cref="Push.Capacity"/> of them nearer than its radius (none outside it is
    /// nearer), the whole reach last. Those beyond the circle count as left out (when only these are in reach, the pushers' pass finds the same
    /// ones).
    /// </summary>
    static void Nearest(World world, Vector3 me, float reach, ref Push q)
    {
        for (float r = NearFirst; ; r *= 2)
        {
            (q.Count, q.Overflow, q.Reach) = (0, false, MathF.Min(r, reach));
            world.Grid.Query(me.X, me.Z, q.Reach, ref q, Gather);
            if (r >= reach) break;
            if (q.Count == q.Capacity && q.Distances[q.Capacity - 1] < r * r)
            {
                q.Overflow = true;
                break;
            }
        }
        q.Reach = reach;
    }

    /// <summary>
    /// The steering velocity of fighter <paramref name="i"/> this tick (units per second, X and Z), with <paramref name="face"/> the flat unit vector
    /// to whom it faces (<see cref="SteerState.Face"/>, else its band target); zero when the band target is gone. The seek and the power-through cone
    /// go along the line to the band target, the side-step snap and the speed caps along the facing (<c>FUN_1402af1e0</c> overwrites its direction
    /// with the look-at's after the forces). <paramref name="strafing"/> is its side-stepping state (the animation's, last tick);
    /// <paramref name="moving"/> whether it moved last tick (the dead zone's STOP or START). Reads the last tick's places only; partition-safe
    /// (writes the fighter's own smoothing).
    /// </summary>
    public static Vector2 Velocity(World world, CombatSystem combat, int i, CharacterCold cold, SteerState s, float maxSpeed, bool strafing,
        bool moving, out Vector2 face)
    {
        face = Vector2.Zero;
        var prev = world.Characters.Previous;
        int t = s.Target;
        if ((uint)t >= (uint)prev.Length || !prev[t].Alive) return Vector2.Zero;
        var me = prev[i].Position;
        var to = prev[t].Position;
        var flat = new Vector2(to.X - me.X, to.Z - me.Z);
        if (flat.LengthSquared() < 1e-6f) return Vector2.Zero;
        var line = Vector2.Normalize(flat);
        face = Facing(prev, i, s, line);
        float mult = cold.Race?.CombatMoveSpeedMult ?? 1;
        float radius = Radius(cold);
        float rs = radius + Radius(world.Characters.Cold(t));
        float d = Vector3.Distance(me, to);

        // The seek: walk in beyond max, back off inside min, else stand.
        float seek = d > rs + s.Max ? (d / (rs + s.Max) - 1) * 100 * 2 * mult : MathF.Min((d / (rs + s.Min) - 1) * 100, 0);

        var (enemyDist, allyDist, stop, start) = Draws(world.Seed, Rng.Key(world.Characters.IdOf(i)), cold);
        var q = new Push
        {
            World = world,
            Combat = combat,
            Me = i,
            Target = t,
            Faction = cold.Faction,
            At = me,
            Radius = rs,
            Own = radius,
            Face = line,
            Turn = s.PowerThrough,
            EnemyReach = rs + enemyDist,
            AllyReach = rs + allyDist,
        };

        // The repulsion takes the nearest MaxRepulsions bodies that push (they push hardest). A body pushes only within both the flocking list's
        // reach and its own reach, so nothing farther than the nearer of the two (plus a large body's radius) is looked at. First the nearest by
        // distance alone, the flocking list's tests only for those; when too many of them are out of it to leave 26, again with the tests on every
        // body (a crowd of the fallen).
        float reach = MathF.Min(radius + FlockReach, MathF.Max(q.EnemyReach, q.AllyReach)) + CombatTuning.DefaultFootprint * 4;
        q.Capacity = Gathered;
        Nearest(world, me, reach, ref q);
        Vector2 enemies = Vector2.Zero, allies = Vector2.Zero;
        int pushes = 0;
        for (int k = 0; k < q.Count && pushes < MaxRepulsions; k++)
            pushes += Add(ref q, q.Slots[k], ref enemies, ref allies);
        if (pushes < MaxRepulsions && q.Overflow)
        {
            (q.Capacity, q.Pushers) = (MaxRepulsions, true);
            Nearest(world, me, reach, ref q);
            (enemies, allies) = (Vector2.Zero, Vector2.Zero);
            for (int k = 0; k < q.Count; k++) Add(ref q, q.Slots[k], ref enemies, ref allies);
        }
        var vel = (enemies + allies * AllyWeight) * RepulsionDamping + line * seek;

        // The dead zone: under STOP while moving, under START while standing, it stands.
        if (!s.PowerThrough && vel.Length() < (moving ? stop : start)) vel = Vector2.Zero;

        // The side-step snap: forward/back, or sideways while strafing; the other part is damped.
        var sideAxis = new Vector2(face.Y, -face.X);
        float z = Vector2.Dot(vel, face), x = Vector2.Dot(vel, sideAxis);
        vel = strafing ? face * (z * StrafeDamping) + sideAxis * x : face * z + sideAxis * (x * StrafeDamping);

        // The caps: facing forward the limit (never over the footwork's 18), backing off 16, sideways 18.
        float speed = vel.Length();
        if (speed > 1e-4f)
        {
            var dir = vel / speed;
            float dot = Vector2.Dot(dir, face);
            float cap = dot > 0.5f ? MathF.Min(s.Limit, maxSpeed) : dot < -0.5f ? BackSpeed : SideSpeed;
            cap = MathF.Min(cap, FootworkCap);
            if (speed > cap) vel = dir * cap;
        }

        vel = new Vector2(Filter(s.HistX, vel.X, world.TickSeconds), Filter(s.HistZ, vel.Y, world.TickSeconds));
        return vel * mult;
    }

    /// <summary>
    /// The flat unit vector to the character it faces, or <paramref name="line"/> (to the band target) when that one is unset, gone or on its spot.
    /// </summary>
    static Vector2 Facing(ReadOnlySpan<CharacterHot> prev, int i, SteerState s, Vector2 line)
    {
        int f = s.Face;
        if (f == s.Target || (uint)f >= (uint)prev.Length || f == i || !prev[f].Alive) return line;
        var flat = new Vector2(prev[f].Position.X - prev[i].Position.X, prev[f].Position.Z - prev[i].Position.Z);
        return flat.LengthSquared() < 1e-6f ? line : Vector2.Normalize(flat);
    }

    /// <summary>
    /// The smoothing of one axis (<c>MotionFilter::Apply</c>): the newest sample first, the weighted mean with weights decaying 0.8 per 60 fps frame,
    /// over the samples of <see cref="FilterLength"/> frames (engine reading for ticks longer than a frame: fewer samples, each weight ratio 0.8^(60
    /// dt)).
    /// </summary>
    internal static float Filter(float[] history, float sample, float dt)
    {
        int n = Math.Clamp((int)MathF.Round(FilterLength / (60 * dt)), 1, FilterLength);
        Array.Copy(history, 0, history, 1, n - 1);
        history[0] = sample;
        float ratio = MathF.Pow(FilterDecay, 60 * dt), w = 1, sum = 0, weights = 0;
        for (int k = 0; k < n; k++)
        {
            sum += history[k] * w;
            weights += w;
            w *= ratio;
        }
        return sum / weights;
    }
}