using System.Numerics;

namespace Meitou.Data.Particles;

/// <summary>
/// One particle ready to draw (docs/formats/particle-universe.md "Drawing"): where, how big, which way a self- or common-oriented
/// billboard points, the texture rotation in radians and the colour (rgb premultiplication is the material's business; alpha is the particle's).
/// 64 bytes, laid out for a vertex buffer: four vec4.
/// </summary>
public struct ParticleInstance
{
    public Vector3 Position;
    public float Rotation;
    /// <summary>The unit direction of travel (oriented_self / perpendicular_self); zero when the particle does not move.</summary>
    public Vector3 Direction;
    public float Width;
    public Vector4 Colour;
    public float Height;
    public float Depth;
    public float Pad0, Pad1;
}

/// <summary>What a simulation reads from outside, per update: the wind (units per second in x and z) and a multiplier on every emission rate.</summary>
public readonly record struct ParticleEnvironment(Vector2 Wind, float WindSpeedMultiplier, float EmissionScale, bool WindAffected, bool WindDirectionEmission)
{
    public static readonly ParticleEnvironment None = new(Vector2.Zero, 1, 1, false, false);
}

/// <summary>
/// The CPU simulation of one ParticleUniverse system (docs/formats/particle-universe.md "Simulation"): each technique has its own particle
/// pool in struct-of-arrays form, its emitters feed it, its affectors and observers change and end particles. Deterministic: the only
/// random sources are the seeded <see cref="System.Random"/> of the emissions and a hash of each particle's own seed (so the affector
/// passes can run in parallel and give the same result). Time advances only through <see cref="Advance"/>.
/// </summary>
public sealed class ParticleSimulation
{
    /// <summary>
    /// Particles above which an affector pass splits over the thread pool. Observed (2026-10-10): at 4000 particles (Heavy_Rain) the split gains nothing
    /// (0.47 ms a frame serial, 0.51 parallel) and, with the pool busy, makes the simulation wait for a helper that has not been scheduled; it pays from some 16000.
    /// </summary>
    const int ParallelThreshold = 16384;
    /// <summary>Slots a technique's pool starts with.</summary>
    const int InitialPool = 128;
    /// <summary>The longest step a single <see cref="Advance"/> takes; a longer interval is split.</summary>
    public const float MaxStep = 1f / 20;

    readonly Random random;
    readonly TechniqueState[] techniques;

    public PuSystemDef Definition { get; }
    /// <summary>The system's place in the world: the emitters' origin and the frame the observers compare positions in. Set it before <see cref="Advance"/>.</summary>
    public Vector3 Origin { get; set; }
    /// <summary>Where the coordinates of the simulation are in the world (added to a particle's position for the observers, which compare world positions). Zero when the simulation is in world coordinates itself.</summary>
    public Vector3 WorldOffset { get; set; }
    /// <summary>The system was stopped (an effect whose life is over): the emitters make nothing more and the particles live out.</summary>
    public bool EmissionStopped { get; set; }
    /// <summary>Seconds since the system started.</summary>
    public float Time { get; private set; }
    public int ParticleCount => techniques.Sum(t => t.Count);
    public IReadOnlyList<PuTechniqueDef> Techniques => Definition.Techniques;

    public ParticleSimulation(PuSystemDef definition, int seed)
    {
        Definition = definition;
        random = new Random(seed);
        techniques = [.. definition.Techniques.Select(t => new TechniqueState(t))];
    }

    sealed class EmitterState
    {
        public float Accumulator;
        /// <summary>Seconds into the emitter's current on/off cycle, and whether it is in its resting part.</summary>
        public float Clock;
        public float Cycle = -1;
        public bool Resting;
        public float Age;
    }

    sealed class TechniqueState
    {
        public readonly PuTechniqueDef Def;
        public readonly EmitterState[] Emitters;
        public int Capacity;
        public int Count;
        public float[] X = [], Y = [], Z = [], Vx = [], Vy = [], Vz = [];
        public float[] Age = [], Life = [], Width = [], Height = [], Depth = [], Rotation = [], RotationSpeed = [];
        public float[] R = [], G = [], B = [], A = [], ER = [], EG = [], EB = [], EA = [];
        public uint[] Seed = [];
        public byte[] Emitter = [];
        public float ObserverClock;
        public readonly float[] RandomiserClock;
        public uint Step;

        public TechniqueState(PuTechniqueDef def)
        {
            Def = def;
            Emitters = [.. def.Emitters.Select(_ => new EmitterState())];
            RandomiserClock = new float[def.Affectors.Count];
            Capacity = Math.Max(def.VisualQuota, 0);
            Resize(Math.Min(Capacity, InitialPool));
        }

        /// <summary>The pool grows with the particles that exist (a quota of 3000 for a system that holds a few hundred costs a few hundred slots), up to the quota.</summary>
        public void Resize(int n)
        {
            Array.Resize(ref X, n); Array.Resize(ref Y, n); Array.Resize(ref Z, n); Array.Resize(ref Vx, n); Array.Resize(ref Vy, n); Array.Resize(ref Vz, n);
            Array.Resize(ref Age, n); Array.Resize(ref Life, n); Array.Resize(ref Width, n); Array.Resize(ref Height, n); Array.Resize(ref Depth, n);
            Array.Resize(ref Rotation, n); Array.Resize(ref RotationSpeed, n);
            Array.Resize(ref R, n); Array.Resize(ref G, n); Array.Resize(ref B, n); Array.Resize(ref A, n);
            Array.Resize(ref ER, n); Array.Resize(ref EG, n); Array.Resize(ref EB, n); Array.Resize(ref EA, n);
            Array.Resize(ref Seed, n); Array.Resize(ref Emitter, n);
        }

        /// <summary>Makes room for one more particle (the caller checked the quota).</summary>
        public void Grow()
        {
            if (Count < X.Length) return;
            Resize(Math.Min(Capacity, Math.Max(X.Length * 2, InitialPool)));
        }

        public void Remove(int i)
        {
            int last = --Count;
            if (i == last) return;
            X[i] = X[last]; Y[i] = Y[last]; Z[i] = Z[last]; Vx[i] = Vx[last]; Vy[i] = Vy[last]; Vz[i] = Vz[last];
            Age[i] = Age[last]; Life[i] = Life[last]; Width[i] = Width[last]; Height[i] = Height[last]; Depth[i] = Depth[last];
            Rotation[i] = Rotation[last]; RotationSpeed[i] = RotationSpeed[last];
            R[i] = R[last]; G[i] = G[last]; B[i] = B[last]; A[i] = A[last]; ER[i] = ER[last]; EG[i] = EG[last]; EB[i] = EB[last]; EA[i] = EA[last];
            Seed[i] = Seed[last]; Emitter[i] = Emitter[last];
        }
    }

    /// <summary>Steps the system by <paramref name="dt"/> seconds (split into steps of at most <see cref="MaxStep"/>).</summary>
    public void Advance(float dt, in ParticleEnvironment environment)
    {
        while (dt > 1e-6f)
        {
            float step = Math.Min(dt, MaxStep);
            Step(step, environment);
            dt -= step;
        }
    }

    /// <summary>The system's fast-forward (<c>fast_forward time interval</c>) or <paramref name="seconds"/> when given: steps in the interval's size so a system starts with its particles in place.</summary>
    public void Prewarm(float seconds, in ParticleEnvironment environment, float interval = 1f / 30)
    {
        for (float t = 0; t < seconds; t += interval) Advance(Math.Min(interval, seconds - t), environment);
    }

    /// <summary>The longest a particle of this system lives (the largest end of an emitter's <c>time_to_live</c>), the natural time to prewarm.</summary>
    public static float LongestLife(PuSystemDef def) =>
        def.Techniques.SelectMany(t => t.Emitters).Select(e => e.Life.Range.Max).DefaultIfEmpty(0).Max();

    void Step(float dt, in ParticleEnvironment env)
    {
        Time += dt;
        foreach (var t in techniques)
        {
            if (!t.Def.Enabled) continue;
            t.Step++;
            if (!EmissionStopped) Emit(t, dt, env);
            Age(t, dt);
            Affect(t, dt, env);
            Move(t, dt, env);
            Observe(t, dt);
        }
    }

    // ---- emission ----

    void Emit(TechniqueState t, float dt, in ParticleEnvironment env)
    {
        var emitters = t.Def.Emitters;
        for (int e = 0; e < emitters.Count; e++)
        {
            var def = emitters[e];
            var st = t.Emitters[e];
            if (!def.Enabled || def.Type is PuEmitterType.Slave or PuEmitterType.Unknown) continue;
            st.Age += dt;
            if (!Working(def, st, dt)) continue;
            float rate = def.Rate.Evaluate(st.Age, random) * env.EmissionScale;
            st.Accumulator += Math.Max(rate, 0) * dt;
            int n = (int)st.Accumulator;
            st.Accumulator -= n;
            for (; n > 0 && t.Count < t.Capacity; n--) Spawn(t, e, def, env);
        }
    }

    /// <summary>The emitter's on/off cycle: it works for <c>duration</c> seconds, then (with a <c>repeat_delay</c>) rests that long and starts again.</summary>
    bool Working(PuEmitterDef def, EmitterState st, float dt)
    {
        if (def.Duration is not { } duration) return true;
        if (st.Cycle < 0) st.Cycle = Math.Max(duration.Evaluate(0, random), 0);
        st.Clock += dt;
        if (!st.Resting)
        {
            if (st.Clock < st.Cycle) return true;
            if (def.RepeatDelay is null) { st.Cycle = float.PositiveInfinity; st.Resting = true; return false; }
            st.Clock = 0;
            st.Cycle = Math.Max(def.RepeatDelay.Evaluate(0, random), 0);
            st.Resting = true;
            return false;
        }
        if (st.Clock >= st.Cycle)
        {
            st.Clock = 0;
            st.Cycle = Math.Max(duration.Evaluate(0, random), 0);
            st.Resting = false;
            return true;
        }
        return false;
    }

    void Spawn(TechniqueState t, int emitterIndex, PuEmitterDef e, in ParticleEnvironment env)
    {
        var scale = Definition.Scale;
        Vector3 offset = Vector3.Zero;
        switch (e.Type)
        {
            case PuEmitterType.Box:
                offset = new Vector3(Centered() * e.BoxSize.X, Centered() * e.BoxSize.Y, Centered() * e.BoxSize.Z) * scale;
                break;
            case PuEmitterType.Circle:
            {
                float angle = e.EmitRandom ? random.NextSingle() * MathF.Tau : (Time / Math.Max(e.Step, 1e-3f)) % MathF.Tau;
                offset = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * (e.Radius * scale.X);
                break;
            }
            case PuEmitterType.SphereSurface:
            {
                float z = random.NextSingle() * 2 - 1, a = random.NextSingle() * MathF.Tau, r = MathF.Sqrt(Math.Max(1 - z * z, 0));
                offset = new Vector3(r * MathF.Cos(a), z, r * MathF.Sin(a)) * (e.Radius * scale.X);
                break;
            }
            case PuEmitterType.Line:
                offset = (e.End * random.NextSingle()) * scale;
                break;
        }
        var direction = e.AutoDirection && offset.LengthSquared() > 1e-12f ? Vector3.Normalize(offset)
            : Cone(e.Direction.LengthSquared() > 1e-12f ? Vector3.Normalize(e.Direction) : Vector3.UnitY, e.Angle.Evaluate(0, random));
        if (env.WindDirectionEmission && env.Wind.LengthSquared() > 1e-6f)
        {
            float horizontal = MathF.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
            var w = Vector2.Normalize(env.Wind) * horizontal;
            direction = new Vector3(w.X, direction.Y, w.Y);
        }
        // The emitter's attributes are read at the system's age (Verified, the plugin's _initParticleVelocity / TimeToLive / Dimensions).
        float speed = e.Velocity.Evaluate(Time, random) * Definition.ScaleVelocity;
        float life = Math.Max(e.Life.Evaluate(Time, random), 1e-3f);
        // all_particle_dimensions is one draw for the three and wins over the per-axis sizes; each per-axis one draws its own.
        float width, height, depth;
        if (e.AllDimensions is not null) width = height = depth = e.AllDimensions.Evaluate(Time, random);
        else if (e.Width is null && e.Height is null && e.Depth is null) (width, height, depth) = (t.Def.DefaultWidth, t.Def.DefaultHeight, t.Def.DefaultDepth);
        else (width, height, depth) = (e.Width?.Evaluate(Time, random) ?? 0, e.Height?.Evaluate(Time, random) ?? 0, e.Depth?.Evaluate(Time, random) ?? 0);
        // A size of 0 is not set (the plugin's setOwnDimensions); the game's pooled particle then keeps its last one, here the technique's default.
        if (width == 0) width = t.Def.DefaultWidth;
        if (height == 0) height = t.Def.DefaultHeight;
        if (depth == 0) depth = t.Def.DefaultDepth;
        var colour = e.Colour;
        if (e.ColourStart is not null || e.ColourEnd is not null)
        {
            var a = e.ColourStart ?? e.Colour;
            var b = e.ColourEnd ?? e.Colour;
            colour = Vector4.Lerp(a, b, random.NextSingle());
        }

        t.Grow();
        int i = t.Count++;
        var local = t.Def.Position + e.Position * scale + offset;
        var p = t.Def.KeepLocal ? local : Origin + local;
        t.X[i] = p.X; t.Y[i] = p.Y; t.Z[i] = p.Z;
        t.Vx[i] = direction.X * speed; t.Vy[i] = direction.Y * speed; t.Vz[i] = direction.Z * speed;
        t.Age[i] = 0; t.Life[i] = life;
        t.Width[i] = width * scale.X; t.Height[i] = height * scale.Y; t.Depth[i] = depth * scale.Z;
        t.R[i] = t.ER[i] = colour.X; t.G[i] = t.EG[i] = colour.Y; t.B[i] = t.EB[i] = colour.Z; t.A[i] = t.EA[i] = colour.W;
        t.Seed[i] = (uint)random.Next();
        t.Emitter[i] = (byte)emitterIndex;
        t.Rotation[i] = 0;
        t.RotationSpeed[i] = 0;
        foreach (var a in t.Def.Affectors)
        {
            if (a.Type != PuAffectorType.TextureRotator || !a.Enabled || a.ExcludedEmitters.Contains(e.Name)) continue;
            // Degrees and degrees per second (Observed: the scripts use 0..360 for the start and small numbers for the speed).
            if (a.Rotation is not null) t.Rotation[i] = a.Rotation.Evaluate(0, random) * (MathF.PI / 180);
            if (a.RotationSpeed is not null) t.RotationSpeed[i] = a.RotationSpeed.Evaluate(0, random) * (MathF.PI / 180);
            break;
        }
    }

    float Centered() => random.NextSingle() - 0.5f;

    /// <summary>A random direction within <paramref name="halfAngle"/> degrees of <paramref name="axis"/> (uniform over the cap).</summary>
    Vector3 Cone(Vector3 axis, float halfAngle)
    {
        if (halfAngle <= 0) return axis;
        float cos = MathF.Cos(Math.Min(halfAngle, 180) * (MathF.PI / 180));
        float c = 1 + (cos - 1) * random.NextSingle();
        float s = MathF.Sqrt(Math.Max(1 - c * c, 0)), phi = random.NextSingle() * MathF.Tau;
        var helper = MathF.Abs(axis.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(axis, helper));
        var v = Vector3.Cross(axis, u);
        return axis * c + (u * MathF.Cos(phi) + v * MathF.Sin(phi)) * s;
    }

    // ---- ageing, affectors, motion ----

    static void Age(TechniqueState t, float dt)
    {
        for (int i = t.Count - 1; i >= 0; i--)
        {
            t.Age[i] += dt;
            if (t.Age[i] >= t.Life[i]) t.Remove(i);
        }
    }

    static void ForRange(int count, Action<int, int> body)
    {
        if (count < ParallelThreshold) { body(0, count); return; }
        const int chunk = 1024;
        Parallel.For(0, (count + chunk - 1) / chunk, c => body(c * chunk, Math.Min((c + 1) * chunk, count)));
    }

    void Affect(TechniqueState t, float dt, in ParticleEnvironment env)
    {
        var affectors = t.Def.Affectors;
        var emitters = t.Def.Emitters;
        for (int k = 0; k < affectors.Count; k++)
        {
            var a = affectors[k];
            if (!a.Enabled) continue;
            bool[]? excluded = null;
            if (a.ExcludedEmitters.Count > 0)
            {
                excluded = new bool[emitters.Count];
                for (int e = 0; e < emitters.Count; e++) excluded[e] = a.ExcludedEmitters.Contains(emitters[e].Name);
            }
            int n = t.Count;
            switch (a.Type)
            {
                case PuAffectorType.Colour when a.TimeColours.Count > 0:
                    ForRange(n, (lo, hi) =>
                    {
                        for (int i = lo; i < hi; i++)
                        {
                            if (excluded?[t.Emitter[i]] == true) continue;
                            var c = Ramp(a.TimeColours, t.Age[i] / t.Life[i]);
                            if (a.MultiplyColour) c *= new Vector4(t.ER[i], t.EG[i], t.EB[i], t.EA[i]);
                            t.R[i] = c.X; t.G[i] = c.Y; t.B[i] = c.Z; t.A[i] = c.W;
                        }
                    });
                    break;
                case PuAffectorType.Scale:
                    ForRange(n, (lo, hi) =>
                    {
                        for (int i = lo; i < hi; i++)
                        {
                            if (excluded?[t.Emitter[i]] == true) continue;
                            // The scale is a rate: units per second, times the system scale, added to the size; xyz_scale alone when it is
                            // given, else x / y / z; read at the life fraction or the system's age. A size that would not stay above 0 is
                            // left as it was (Verified, the plugin's ScaleAffector::_affect and setOwnDimensions).
                            float f = a.SinceStartSystem ? Time : t.Age[i] / t.Life[i];
                            var s = Definition.Scale * dt;
                            float rx, ry, rz;
                            if (a.ScaleXyz is not null) rx = ry = rz = a.ScaleXyz.Evaluate(f);
                            else (rx, ry, rz) = (a.ScaleX?.Evaluate(f) ?? 0, a.ScaleY?.Evaluate(f) ?? 0, a.ScaleZ?.Evaluate(f) ?? 0);
                            float w = t.Width[i] + rx * s.X, h = t.Height[i] + ry * s.Y, d = t.Depth[i] + rz * s.Z;
                            if (w > 0) t.Width[i] = w;
                            if (h > 0) t.Height[i] = h;
                            if (d > 0) t.Depth[i] = d;
                        }
                    });
                    break;
                case PuAffectorType.TextureRotator:
                    ForRange(n, (lo, hi) =>
                    {
                        for (int i = lo; i < hi; i++)
                            if (excluded?[t.Emitter[i]] != true) t.Rotation[i] += t.RotationSpeed[i] * dt;
                    });
                    break;
                case PuAffectorType.LinearForce:
                {
                    var f = a.Force * dt;
                    bool average = a.AverageForce;
                    ForRange(n, (lo, hi) =>
                    {
                        for (int i = lo; i < hi; i++)
                        {
                            if (excluded?[t.Emitter[i]] == true) continue;
                            if (average)
                            {
                                // "average": the velocity moves halfway towards the force vector's direction at its strength
                                t.Vx[i] = (t.Vx[i] + a.Force.X) * 0.5f; t.Vy[i] = (t.Vy[i] + a.Force.Y) * 0.5f; t.Vz[i] = (t.Vz[i] + a.Force.Z) * 0.5f;
                            }
                            else { t.Vx[i] += f.X; t.Vy[i] += f.Y; t.Vz[i] += f.Z; }
                        }
                    });
                    break;
                }
                case PuAffectorType.Vortex:
                {
                    var axis = a.Axis.LengthSquared() > 1e-12f ? Vector3.Normalize(a.Axis) : Vector3.UnitY;
                    var centre = Origin + a.Position * Definition.Scale;
                    if (t.Def.KeepLocal) centre = a.Position * Definition.Scale;
                    ForRange(n, (lo, hi) =>
                    {
                        for (int i = lo; i < hi; i++)
                        {
                            if (excluded?[t.Emitter[i]] == true) continue;
                            // Rotates the particle about the axis through the affector's position, speed in radians per second (Unknown: the unit).
                            float speed = a.RotationSpeed?.Evaluate(t.Age[i] / t.Life[i]) ?? 0;
                            var q = Quaternion.CreateFromAxisAngle(axis, speed * dt);
                            var p = Vector3.Transform(new Vector3(t.X[i], t.Y[i], t.Z[i]) - centre, q) + centre;
                            t.X[i] = p.X; t.Y[i] = p.Y; t.Z[i] = p.Z;
                            var v = Vector3.Transform(new Vector3(t.Vx[i], t.Vy[i], t.Vz[i]), q);
                            t.Vx[i] = v.X; t.Vy[i] = v.Y; t.Vz[i] = v.Z;
                        }
                    });
                    break;
                }
                case PuAffectorType.Gravity:
                {
                    // Attracts towards the affector's position (Unknown: the strength law; here g / max(d, 1)).
                    var centre = (t.Def.KeepLocal ? Vector3.Zero : Origin) + a.Position * Definition.Scale;
                    float g = a.Strength?.Evaluate(0) ?? 0;
                    ForRange(n, (lo, hi) =>
                    {
                        for (int i = lo; i < hi; i++)
                        {
                            if (excluded?[t.Emitter[i]] == true) continue;
                            var d = centre - new Vector3(t.X[i], t.Y[i], t.Z[i]);
                            float len = Math.Max(d.Length(), 1);
                            var dv = d / len * (g / len) * dt;
                            t.Vx[i] += dv.X; t.Vy[i] += dv.Y; t.Vz[i] += dv.Z;
                        }
                    });
                    break;
                }
                case PuAffectorType.Jet:
                {
                    float acc = a.Strength?.Evaluate(0) ?? 0;
                    ForRange(n, (lo, hi) =>
                    {
                        for (int i = lo; i < hi; i++)
                        {
                            if (excluded?[t.Emitter[i]] == true) continue;
                            var v = new Vector3(t.Vx[i], t.Vy[i], t.Vz[i]);
                            float len = v.Length();
                            if (len < 1e-4f) continue;
                            v += v / len * acc * dt;
                            t.Vx[i] = v.X; t.Vy[i] = v.Y; t.Vz[i] = v.Z;
                        }
                    });
                    break;
                }
                case PuAffectorType.SineForce:
                {
                    // force_vector · sin(2π f t) with f between min and max frequency by the particle's own seed (Unknown: the exact form).
                    float tt = Time;
                    ForRange(n, (lo, hi) =>
                    {
                        for (int i = lo; i < hi; i++)
                        {
                            if (excluded?[t.Emitter[i]] == true) continue;
                            float f = a.MinFrequency + (a.MaxFrequency - a.MinFrequency) * Hash01(t.Seed[i]);
                            var dv = a.Force * (MathF.Sin(MathF.Tau * f * tt) * dt);
                            t.Vx[i] += dv.X; t.Vy[i] += dv.Y; t.Vz[i] += dv.Z;
                        }
                    });
                    break;
                }
                case PuAffectorType.Randomiser:
                {
                    // Every time_step seconds (each step when 0) the particle's position is nudged by a random amount up to ± max_deviation
                    // per axis; a step shorter than 1/60 s is scaled to it, so the drift does not depend on the frame rate (Observed: the
                    // scripts' small deviations make a falling flake flutter; the plugin's exact rule is Unknown).
                    float stepTime = a.TimeStep;
                    float scale = 1;
                    if (stepTime > 0)
                    {
                        t.RandomiserClock[k] += dt;
                        if (t.RandomiserClock[k] < stepTime) break;
                        t.RandomiserClock[k] -= stepTime;
                    }
                    else scale = Math.Min(dt * 60, 4);
                    uint step = t.Step * 2654435761u + (uint)k * 40503u;
                    var dev = a.MaxDeviation * scale;
                    ForRange(n, (lo, hi) =>
                    {
                        for (int i = lo; i < hi; i++)
                        {
                            if (excluded?[t.Emitter[i]] == true) continue;
                            uint s = t.Seed[i] ^ step;
                            t.X[i] += (Hash01(s * 3 + 1) * 2 - 1) * dev.X;
                            t.Y[i] += (Hash01(s * 3 + 2) * 2 - 1) * dev.Y;
                            t.Z[i] += (Hash01(s * 3 + 3) * 2 - 1) * dev.Z;
                        }
                    });
                    break;
                }
            }
        }
    }

    static void Move(TechniqueState t, float dt, in ParticleEnvironment env)
    {
        var wind = env.WindAffected ? env.Wind * env.WindSpeedMultiplier * dt : Vector2.Zero;
        ForRange(t.Count, (lo, hi) =>
        {
            for (int i = lo; i < hi; i++)
            {
                t.X[i] += t.Vx[i] * dt + wind.X;
                t.Y[i] += t.Vy[i] * dt;
                t.Z[i] += t.Vz[i] * dt + wind.Y;
            }
        });
    }

    // ---- observers ----

    void Observe(TechniqueState t, float dt)
    {
        t.ObserverClock += dt;
        foreach (var o in t.Def.Observers)
        {
            if (!o.Enabled || !o.Handlers.Contains("DoExpire")) continue;
            if (o.Interval > 0 && t.ObserverClock < o.Interval) continue;
            if (o.Type == PuObserverType.OnPosition && o.Axis >= 0)
            {
                // The particle's own position: world space unless the technique keeps its particles local (Observed: the plugin compares the position attribute).
                float shift = t.Def.KeepLocal ? 0 : o.Axis switch { 0 => WorldOffset.X, 1 => WorldOffset.Y, _ => WorldOffset.Z };
                var axis = o.Axis switch { 0 => t.X, 1 => t.Y, _ => t.Z };
                for (int i = t.Count - 1; i >= 0; i--)
                {
                    float v = axis[i] + shift;
                    if (o.Compare == PuCompare.LessThan ? v < o.Value : o.Compare == PuCompare.GreaterThan ? v > o.Value : Math.Abs(v - o.Value) < 1e-3f)
                        t.Remove(i);
                }
            }
            else if (o.Type == PuObserverType.OnTime)
            {
                // Expires particles by age (or the system's age with since_start_system).
                for (int i = t.Count - 1; i >= 0; i--)
                {
                    float v = o.SinceStartSystem ? Time : t.Age[i];
                    if (o.Compare == PuCompare.GreaterThan ? v > o.Value : o.Compare == PuCompare.LessThan && v < o.Value) t.Remove(i);
                }
            }
        }
        if (t.Def.Observers.Count > 0 && t.ObserverClock >= Math.Max(t.Def.Observers.Max(o => o.Interval), 0)) t.ObserverClock = 0;
    }

    /// <summary>Moves every world-space particle by <paramref name="delta"/> (re-basing the simulation's coordinates; the caller shifts <see cref="Origin"/> alike).</summary>
    public void Translate(Vector3 delta)
    {
        foreach (var t in techniques)
        {
            if (t.Def.KeepLocal) continue;
            for (int i = 0; i < t.Count; i++) { t.X[i] += delta.X; t.Y[i] += delta.Y; t.Z[i] += delta.Z; }
        }
    }

    // ---- wrapping (camera groups) ----

    /// <summary>
    /// Keeps the world-space particles of every technique in the cube of half-extent <paramref name="halfExtent"/> round <paramref name="centre"/> by
    /// wrapping them modulo its edge (docs/formats/weather.md "Spawning": the camera group's cube); local particles are left alone.
    /// </summary>
    public void Wrap(Vector3 centre, float halfExtent)
    {
        float edge = halfExtent * 2;
        foreach (var t in techniques)
        {
            if (t.Def.KeepLocal) continue;
            for (int i = 0; i < t.Count; i++)
            {
                t.X[i] = WrapAxis(t.X[i] - centre.X, halfExtent, edge) + centre.X;
                t.Y[i] = WrapAxis(t.Y[i] - centre.Y, halfExtent, edge) + centre.Y;
                t.Z[i] = WrapAxis(t.Z[i] - centre.Z, halfExtent, edge) + centre.Z;
            }
        }
    }

    /// <summary>
    /// The global effect's wrap (docs/formats/weather.md "Global"; Verified (decompiled), FUN_140101be0, in part): a particle farther than
    /// <paramref name="radius"/> from <paramref name="centre"/> is put at a random place within ±<paramref name="radius"/> of the centre in x and z, at
    /// the centre's height; no particle stays above <paramref name="maxHeight"/> (infinity: no limit). The game also first pushes the particle back by
    /// 1.5 radius along a direction it keeps in a global (Unknown: not modelled).
    /// </summary>
    public void WrapSphere(Vector3 centre, float radius, float maxHeight, int salt)
    {
        float r2 = radius * radius;
        foreach (var t in techniques)
        {
            if (t.Def.KeepLocal) continue;
            for (int i = 0; i < t.Count; i++)
            {
                float dx = t.X[i] - centre.X, dy = t.Y[i] - centre.Y, dz = t.Z[i] - centre.Z;
                if (dx * dx + dy * dy + dz * dz > r2)
                {
                    uint s = t.Seed[i] ^ (uint)salt * 2246822519u ^ t.Step * 3266489917u;
                    t.X[i] = centre.X + (Hash01(s * 3 + 1) * 2 - 1) * radius;
                    t.Y[i] = centre.Y;
                    t.Z[i] = centre.Z + (Hash01(s * 3 + 2) * 2 - 1) * radius;
                }
                if (t.Y[i] > maxHeight) t.Y[i] = maxHeight;
            }
        }
    }

    static float WrapAxis(float v, float half, float edge)
    {
        if (v >= -half && v < half) return v;
        float m = (v + half) % edge;
        if (m < 0) m += edge;
        return m - half;
    }

    // ---- reading ----

    /// <summary>Technique <paramref name="index"/>'s particles into <paramref name="destination"/> (at least its <see cref="TechniqueParticleCount"/> long); returns how many were written. The colour is multiplied by <paramref name="tint"/> (rgb) and <paramref name="alpha"/>; <paramref name="worldOffset"/> is added to every position (the sim keeps small numbers near its own origin; the caller adds where that origin is).</summary>
    public int Collect(int index, Span<ParticleInstance> destination, Vector3 tint, float alpha, Vector3 worldOffset = default)
    {
        var t = techniques[index];
        int n = Math.Min(t.Count, destination.Length);
        var offset = (t.Def.KeepLocal ? Origin : Vector3.Zero) + worldOffset;
        for (int i = 0; i < n; i++)
        {
            var v = new Vector3(t.Vx[i], t.Vy[i], t.Vz[i]);
            float len = v.Length();
            destination[i] = new ParticleInstance
            {
                Position = new Vector3(t.X[i], t.Y[i], t.Z[i]) + offset,
                Rotation = t.Rotation[i],
                Direction = len > 1e-4f ? v / len : Vector3.Zero,
                Width = t.Width[i], Height = t.Height[i], Depth = t.Depth[i],
                Colour = new Vector4(t.R[i] * tint.X, t.G[i] * tint.Y, t.B[i] * tint.Z, t.A[i] * alpha),
            };
        }
        return n;
    }

    public int TechniqueParticleCount(int index) => techniques[index].Count;

    /// <summary>Particle positions of a technique (for tests): copies of x, y, z.</summary>
    public (float[] X, float[] Y, float[] Z) Positions(int index)
    {
        var t = techniques[index];
        return (t.X[..t.Count], t.Y[..t.Count], t.Z[..t.Count]);
    }

    public (float[] Age, float[] Life) Ages(int index)
    {
        var t = techniques[index];
        return (t.Age[..t.Count], t.Life[..t.Count]);
    }

    static Vector4 Ramp(IReadOnlyList<(float Time, Vector4 Colour)> points, float f)
    {
        if (f <= points[0].Time) return points[0].Colour;
        for (int i = 1; i < points.Count; i++)
            if (f <= points[i].Time)
            {
                float span = points[i].Time - points[i - 1].Time;
                return span <= 1e-9f ? points[i].Colour : Vector4.Lerp(points[i - 1].Colour, points[i].Colour, (f - points[i - 1].Time) / span);
            }
        return points[^1].Colour;
    }

    /// <summary>A hash of <paramref name="x"/> in [0, 1) (the particles' own random numbers, so parallel passes stay deterministic).</summary>
    static float Hash01(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16;
        return (x >> 8) * (1f / (1 << 24));
    }
}
